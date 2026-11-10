using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Decorator that puts every outbound call of an <see cref="IPaymentGateway"/> behind <see cref="GatewayResilience"/>,
/// so a future VNPay adapter gets timeouts, retries and the breaker for free. Only calls that are safe to repeat are
/// wrapped: creating a session and refunding need an idempotency key (rejected when blank), reading a status is read-only.
/// <see cref="VerifyAndParse"/> is pure and passes straight through.
/// </summary>
public sealed class ResilientPaymentGateway(IPaymentGateway inner, GatewayResilience resilience) : IPaymentGateway
{
    public string Provider => inner.Provider;

    public Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
    {
        RequireKey(idempotencyKey);
        return resilience.ExecuteAsync(token => inner.CreateSessionAsync(order, returnUrl, expiresAt, idempotencyKey, token), ct);
    }

    public Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct) =>
        resilience.ExecuteAsync(token => inner.GetStatusAsync(sessionId, token), ct);

    public Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
    {
        RequireKey(idempotencyKey);
        return resilience.ExecuteAsync(token => inner.RefundAsync(sessionId, amount, idempotencyKey, token), ct);
    }

    public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) => inner.VerifyAndParse(headers, body);

    private static void RequireKey(string idempotencyKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
}
