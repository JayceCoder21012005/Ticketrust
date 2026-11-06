using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>
/// One gateway session for an order. It expires <see cref="GatewayMargin"/> before the order does, so the gateway
/// normally refuses payment after the hold dies. Webhooks can arrive out of order, so
/// <see cref="MarkSucceeded"/> is legal from every status except <c>Succeeded</c> (like <c>Order.MarkPaid</c>);
/// <see cref="MarkFailed"/> and <see cref="MarkExpired"/> are legal only from <c>Pending</c>.
/// </summary>
public sealed class PaymentAttempt : AggregateRoot, IAuditable
{
    public static readonly TimeSpan GatewayMargin = TimeSpan.FromSeconds(30);

    /// <summary>Column width of <see cref="RedirectUrl"/>.</summary>
    public const int MaxRedirectUrlLength = 2000;

    private PaymentAttempt(
        Guid orderId,
        string provider,
        string providerSessionId,
        string redirectUrl,
        Money amount,
        DateTimeOffset expiresAt)
        : base(Guid.CreateVersion7())
    {
        OrderId = orderId;
        Provider = provider;
        ProviderSessionId = providerSessionId;
        RedirectUrl = redirectUrl;
        Amount = amount;
        ExpiresAt = expiresAt;
        Status = PaymentAttemptStatus.Pending;
    }

    // Required by EF Core.
    private PaymentAttempt()
    {
        Provider = string.Empty;
        ProviderSessionId = string.Empty;
        RedirectUrl = string.Empty;
        Amount = Money.Zero();
    }

    public Guid OrderId { get; private set; }

    public string Provider { get; private set; }

    public string ProviderSessionId { get; private set; }

    /// <summary>Where the buyer pays, as the gateway returned it; a repeated pay request sends the buyer back to the same page.</summary>
    public string RedirectUrl { get; private set; }

    public Money Amount { get; private set; }

    public PaymentAttemptStatus Status { get; private set; }

    /// <summary>Order expiry minus <see cref="GatewayMargin"/>.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PaymentAttempt Create(
        Guid orderId,
        string provider,
        string providerSessionId,
        string redirectUrl,
        Money amount,
        DateTimeOffset orderExpiresAt,
        DateTimeOffset now)
    {
        if (orderId == Guid.Empty || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerSessionId) || !IsHttpUrl(redirectUrl))
        {
            throw new DomainException("payment.invalid_attempt", "A payment attempt needs an order, a provider, a session id and an http(s) redirect URL.");
        }

        var expiresAt = orderExpiresAt.ToUniversalTime() - GatewayMargin;
        if (expiresAt <= now)
        {
            throw new DomainException("payment.window_closed", "Too little time is left on the order to start a payment.");
        }

        return new PaymentAttempt(orderId, provider.Trim(), providerSessionId.Trim(), redirectUrl.Trim(), amount, expiresAt);
    }

    private static bool IsHttpUrl(string? value) =>
        value?.Trim() is { Length: <= MaxRedirectUrlLength } url
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";

    /// <summary>True while a buyer can still be sent to this session: pending and not yet past <see cref="ExpiresAt"/>.</summary>
    public bool IsLive(DateTimeOffset now) => Status == PaymentAttemptStatus.Pending && now < ExpiresAt;

    public void MarkSucceeded()
    {
        if (Status == PaymentAttemptStatus.Succeeded)
        {
            throw IllegalTransition(nameof(MarkSucceeded));
        }

        Status = PaymentAttemptStatus.Succeeded;
    }

    public void MarkFailed() => LeavePending(PaymentAttemptStatus.Failed, nameof(MarkFailed));

    public void MarkExpired() => LeavePending(PaymentAttemptStatus.Expired, nameof(MarkExpired));

    private void LeavePending(PaymentAttemptStatus to, string method)
    {
        if (Status != PaymentAttemptStatus.Pending)
        {
            throw IllegalTransition(method);
        }

        Status = to;
    }

    private DomainException IllegalTransition(string method) =>
        new("payment.illegal_transition", $"Cannot {method} a payment attempt that is {Status}.");
}
