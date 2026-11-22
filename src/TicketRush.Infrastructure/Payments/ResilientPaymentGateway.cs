using System.Diagnostics;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Decorator that puts every outbound call of an <see cref="IPaymentGateway"/> behind <see cref="GatewayResilience"/>,
/// so a future VNPay adapter gets timeouts, retries and the breaker for free. Only calls that are safe to repeat are
/// wrapped: creating a session and refunding need an idempotency key (rejected when blank), reading a status is read-only.
/// <see cref="VerifyAndParse"/> is pure and passes straight through. Each call, retries included, is timed into
/// <c>ticketrush.payment.gateway.duration</c> (spec 17).
/// </summary>
public sealed class ResilientPaymentGateway(IPaymentGateway inner, GatewayResilience resilience, ISalesMetrics? metrics = null) : IPaymentGateway
{
    public string Provider => inner.Provider;

    public Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
    {
        RequireKey(idempotencyKey);
        return TimedAsync("create_session", () => resilience.ExecuteAsync(token => inner.CreateSessionAsync(order, returnUrl, expiresAt, idempotencyKey, token), ct));
    }

    public Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct) =>
        TimedAsync("get_status", () => resilience.ExecuteAsync(token => inner.GetStatusAsync(sessionId, token), ct));

    public Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
    {
        RequireKey(idempotencyKey);
        return TimedAsync("refund", () => resilience.ExecuteAsync(token => inner.RefundAsync(sessionId, amount, idempotencyKey, token), ct));
    }

    private async Task<T> TimedAsync<T>(string operation, Func<Task<T>> call)
    {
        var started = Stopwatch.GetTimestamp();
        var succeeded = false;
        try
        {
            var result = await call();
            succeeded = true;
            return result;
        }
        finally
        {
            metrics?.GatewayCall(operation, Stopwatch.GetElapsedTime(started), succeeded);
        }
    }

    public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body) => inner.VerifyAndParse(headers, body);

    private static void RequireKey(string idempotencyKey) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
}
