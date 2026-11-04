using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Stand-in payment provider for development, demos and tests (spec 08). Sessions live in Redis (see
/// <see cref="MockSessionStore"/>), so the api and the worker share them. A session stays <c>Pending</c> until
/// <see cref="MockGatewayControl"/> completes it (the mock page's buttons); webhooks are signed like a real provider's.
/// </summary>
public sealed class MockPaymentGateway(
    MockSessionStore sessions,
    IOptionsMonitor<MockGatewayOptions> options,
    TimeProvider time) : IPaymentGateway
{
    public const string ProviderName = "mock";

    private bool? _outageOverride;

    public string Provider => ProviderName;

    /// <summary>Fault injection (C11). Defaults to <c>Payments:Mock:SimulateOutage</c>; tests can set it directly.</summary>
    public bool SimulateOutage
    {
        get => _outageOverride ?? options.CurrentValue.SimulateOutage;
        set => _outageOverride = value;
    }

    public async Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ThrowIfOutage();
        var now = time.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new PaymentRejectedException("The payment session would already be expired.");
        }

        var session = await sessions.CreateAsync(order, returnUrl, expiresAt, idempotencyKey, now);
        var redirect = new Uri($"{options.CurrentValue.WebBaseUrl.TrimEnd('/')}/mock-gateway/{session.SessionId}");
        return new PaymentSession(session.SessionId, redirect, session.ExpiresAt);
    }

    public async Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct)
    {
        ThrowIfOutage();
        var session = await sessions.GetAsync(sessionId);
        return session?.EffectiveStatus(time.GetUtcNow()) ?? PaymentStatus.Expired;
    }

    public async Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ThrowIfOutage();
        if (await sessions.GetRefundAsync(idempotencyKey) is { } done)
        {
            return done;
        }

        var session = await sessions.GetAsync(sessionId);
        return RefusalReason(session, amount) is { } reason
            ? new RefundResult(string.Empty, RefundStatus.Failed, reason)
            : await sessions.RecordRefundAsync(sessionId, idempotencyKey, amount.Amount);
    }

    public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        WebhookSignature.Verify(headers.Get(WebhookHeaders.SignatureHeader), body, options.CurrentValue.WebhookSecret, time.GetUtcNow(), WebhookTolerance.Window);
        return Parse(body);
    }

    private static string? RefusalReason(MockSession? session, Money amount)
    {
        if (session is null)
        {
            return "session_not_found";
        }

        if (session.Status != MockSessionStatus.Succeeded)
        {
            return "payment_not_succeeded";
        }

        if (amount.Amount <= 0 || amount.Currency != session.Amount.Currency)
        {
            return "invalid_amount";
        }

        return amount.Amount > session.Amount.Amount - session.RefundedAmount ? "amount_exceeds_payment" : null;
    }

    private static WebhookEvent Parse(string body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<MockWebhookPayload>(body, MockWebhookPayload.Json)
                ?? throw new InvalidWebhookPayloadException("Empty webhook body.");
            if (string.IsNullOrWhiteSpace(payload.Id) || string.IsNullOrWhiteSpace(payload.SessionId))
            {
                throw new InvalidWebhookPayloadException("Webhook id and sessionId are required.");
            }

            var amount = payload.Amount is { } value ? new Money(value, payload.Currency ?? Money.DefaultCurrency) : null;
            return new WebhookEvent(payload.Id, payload.SessionId, TypeOf(payload.Type), amount, payload.OccurredAt);
        }
        catch (Exception ex) when (ex is JsonException or DomainException)
        {
            throw new InvalidWebhookPayloadException("Webhook body is not a valid event.", ex);
        }
    }

    private static WebhookEventType TypeOf(string type) => type switch
    {
        MockWebhookPayload.SucceededType => WebhookEventType.Succeeded,
        MockWebhookPayload.FailedType => WebhookEventType.Failed,
        MockWebhookPayload.RefundedType => WebhookEventType.Refunded,
        _ => throw new InvalidWebhookPayloadException($"Unknown webhook type '{type}'."),
    };

    private void ThrowIfOutage()
    {
        if (SimulateOutage)
        {
            throw new HttpRequestException("Simulated payment gateway outage.");
        }
    }
}
