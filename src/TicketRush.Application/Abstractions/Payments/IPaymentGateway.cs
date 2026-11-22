using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Payments;

/// <summary>
/// Payment provider port (spec 08, Strategy). Adapters live in Infrastructure; <c>MockPaymentGateway</c> is the only
/// one today and a VNPay adapter can follow. Implementations that talk over the network are wrapped by
/// <c>ResilientPaymentGateway</c> (timeout, retries, circuit breaker), so every method that is retried takes an
/// idempotency key or is read-only.
/// </summary>
public interface IPaymentGateway
{
    /// <summary>Stable provider name; also the <c>{provider}</c> segment of <c>/webhooks/payments/{provider}</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Opens a payment session for the order. The same <paramref name="idempotencyKey"/> returns the same session, so a
    /// double click or a retry never creates a second charge. Throws <see cref="PaymentRejectedException"/> when the
    /// gateway refuses the request (for example <paramref name="expiresAt"/> already passed).
    /// </summary>
    Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct);

    /// <summary>Current outcome of the session; used by the expiry service before it expires an order.</summary>
    Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct);

    /// <summary>Refunds (part of) a succeeded payment. Idempotent per <paramref name="idempotencyKey"/>.</summary>
    /// <remarks>
    /// The refund worker retries a DECLINED refund with the SAME key (the order id, or <c>attempt:{id}</c>). That assumes the
    /// gateway only replays a stored SUCCESS for a key and evaluates a declined request afresh, as the mock does. Providers
    /// that store and replay the first response for a key, failures included (Stripe-style idempotency), would decline
    /// forever: such an adapter must derive a fresh provider key per retry (e.g. key + attempt number) while still asking
    /// the provider whether an earlier try for this refund succeeded, so a success is never repeated.
    /// </remarks>
    Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Verifies the provider's signature and replay window (<see cref="WebhookTolerance.Window"/>) and parses the body.
    /// Pure and synchronous: never wrapped by the resilience pipeline.
    /// </summary>
    /// <exception cref="InvalidWebhookSignatureException">Missing, malformed or wrong signature, or a timestamp outside the window.</exception>
    /// <exception cref="InvalidWebhookPayloadException">The signature is valid but the body is not a known event.</exception>
    WebhookEvent VerifyAndParse(WebhookHeaders headers, string body);

    /// <summary>
    /// True when the provider wants its webhook acknowledged with <c>204 No Content</c> and no body (MoMo, spec 18); otherwise the
    /// receiver answers 200 with a receipt.
    /// </summary>
    bool AcknowledgesWithNoContent => false;
}

/// <param name="SessionId">Provider session id; stored as <c>PaymentAttempt.ProviderSessionId</c>.</param>
/// <param name="RedirectUrl">Where the buyer pays (for the mock: the web's <c>/mock-gateway/{sessionId}</c>).</param>
public sealed record PaymentSession(string SessionId, Uri RedirectUrl, DateTimeOffset ExpiresAt);

/// <summary>Outcome of a session. A session that is unknown to the provider or past its expiry reads as <see cref="Expired"/>.</summary>
public enum PaymentStatus
{
    Pending,
    Succeeded,
    Failed,
    Expired,
}

public enum RefundStatus
{
    Succeeded,
    Failed,
}

/// <param name="RefundId">Provider refund id (same for every call with the same idempotency key).</param>
/// <param name="Status">Whether the refund went through.</param>
/// <param name="FailureReason">Machine readable reason when <paramref name="Status"/> is <see cref="RefundStatus.Failed"/>.</param>
public sealed record RefundResult(string RefundId, RefundStatus Status, string? FailureReason = null)
{
    public bool IsSuccess => Status == RefundStatus.Succeeded;
}

public enum WebhookEventType
{
    Succeeded,
    Failed,
    Refunded,
}

/// <param name="ProviderEventId">Unique per provider event; the dedupe key of <c>processed_webhooks</c> (C4).</param>
/// <param name="SessionId">Provider session the event is about.</param>
/// <param name="Type">What happened.</param>
/// <param name="Amount">Amount the event is about, when the provider sends one.</param>
/// <param name="OccurredAt">When the provider says it happened.</param>
public sealed record WebhookEvent(
    string ProviderEventId,
    string SessionId,
    WebhookEventType Type,
    Money? Amount,
    DateTimeOffset OccurredAt);
