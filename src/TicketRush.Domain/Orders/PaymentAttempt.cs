using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>
/// One gateway session for an order. It expires <see cref="GatewayMargin"/> before the order does, so the gateway
/// normally refuses payment after the hold dies. Webhooks can arrive out of order, so
/// <see cref="MarkSucceeded"/> is legal from Pending, Failed and Expired (like <c>Order.MarkPaid</c>);
/// <see cref="MarkFailed"/> and <see cref="MarkExpired"/> are legal only from <c>Pending</c>.
/// <para>
/// Invariant kept by the webhook handler: an order has at most one <c>Succeeded</c> attempt, the one whose money paid it
/// (or, for an unfulfillable order, the one the order-level refund sends back). Money on any other session of the order
/// goes back through <see cref="RequestRefund"/>.
/// </para>
/// </summary>
public sealed class PaymentAttempt : AggregateRoot, IAuditable, IRefundable
{
    public static readonly TimeSpan GatewayMargin = TimeSpan.FromSeconds(30);

    /// <summary>Column width of <see cref="RedirectUrl"/>.</summary>
    public const int MaxRedirectUrlLength = 2000;

    // What RequestRefund promised to send back (the gateway may have collected less than Amount). Mapped as two columns.
    private long? _refundAmount;
    private string? _refundCurrency;

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

    /// <summary>What <see cref="RequestRefund"/> promised to send back; <c>null</c> unless a session-level refund was requested.</summary>
    public Money? RefundAmount => _refundAmount is { } amount && _refundCurrency is { } currency ? new Money(amount, currency) : null;

    /// <summary>The refund worker's retry state while the session is <see cref="PaymentAttemptStatus.RefundPending"/>.</summary>
    public RefundTracking Refund { get; private set; } = RefundTracking.None;

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

    /// <summary>This session's money pays for the order. Legal from Pending, Failed and Expired (webhooks arrive out of order).</summary>
    public void MarkSucceeded()
    {
        EnsureMoneyNotSettled(nameof(MarkSucceeded));
        Status = PaymentAttemptStatus.Succeeded;
    }

    /// <summary>
    /// Pending, Failed or Expired → RefundPending: money reached this session but it cannot pay for the order (a second
    /// payment of a paid order, a closed order, a wrong amount). Raises <see cref="PaymentRefundRequested"/> for
    /// <paramref name="amount"/>; the order itself is not touched.
    /// </summary>
    public void RequestRefund(string reason, Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("payment.invalid_refund", "A refund needs a reason.");
        }

        EnsureMoneyNotSettled(nameof(RequestRefund));
        Status = PaymentAttemptStatus.RefundPending;
        _refundAmount = amount.Amount;
        _refundCurrency = amount.Currency;
        Raise(new PaymentRefundRequested(Id, OrderId, Provider, ProviderSessionId, amount.Amount, amount.Currency, reason));
    }

    /// <summary>RefundPending → Refunded once the gateway confirmed the refund of this session. Raises <see cref="PaymentRefunded"/>.</summary>
    public void MarkRefunded()
    {
        EnsureRefundPending(nameof(MarkRefunded));
        Status = PaymentAttemptStatus.Refunded;
        var refunded = RefundAmount ?? Amount;
        Raise(new PaymentRefunded(Id, OrderId, refunded.Amount, refunded.Currency));
    }

    /// <inheritdoc />
    public void RecordRefundDecline(string error, DateTimeOffset? retryAt) => Track(Refund.Declined(error, retryAt), nameof(RecordRefundDecline));

    /// <inheritdoc />
    public void ParkRefund(string error) => Track(Refund.Parked(error), nameof(ParkRefund));

    /// <inheritdoc />
    public void ResumeRefund() => Track(Refund.Resumed(), nameof(ResumeRefund));

    private void Track(RefundTracking next, string method)
    {
        EnsureRefundPending(method);
        Refund = next;
    }

    private void EnsureRefundPending(string method)
    {
        if (Status != PaymentAttemptStatus.RefundPending)
        {
            throw IllegalTransition(method);
        }
    }

    /// <summary>A session's money is decided once: it either pays the order (Succeeded) or goes back (RefundPending, Refunded).</summary>
    private void EnsureMoneyNotSettled(string method)
    {
        if (Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.RefundPending or PaymentAttemptStatus.Refunded)
        {
            throw IllegalTransition(method);
        }
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
