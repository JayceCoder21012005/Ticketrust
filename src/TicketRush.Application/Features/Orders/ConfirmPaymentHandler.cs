using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

/// <summary>
/// <c>POST /orders/{id}/payment/confirm</c> (spec 18): the buyer is back from the gateway's page, so ask the gateway now instead
/// of waiting for its webhook (which may be late, or, for MoMo on a laptop, never come). For an order still waiting on its
/// money, every Pending session is asked, newest first; a <see cref="PaymentStatus.Succeeded"/> one runs the webhook decision
/// table with the same synthetic event the expiry sweep uses (<see cref="FulfillOrderHandler.StatusEventId"/>), so a webhook
/// for the same payment later decides nothing twice. What the buyer's browser says is never trusted. An outage changes nothing:
/// the page keeps polling.
/// </summary>
public sealed partial class ConfirmPaymentHandler(
    IAppDbContext db,
    IPaymentGatewayResolver gateways,
    FulfillOrderHandler fulfil,
    GetOrderHandler orders,
    TimeProvider time,
    ILogger<ConfirmPaymentHandler> logger)
{
    public async Task<Result<OrderResponse>> HandleAsync(Guid userId, Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        if (order.Status is OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed)
        {
            await ConfirmAsync(order.Id, ct);
        }

        return await orders.HandleAsync(userId, orderId, ct);
    }

    private async Task ConfirmAsync(Guid orderId, CancellationToken ct)
    {
        var attempts = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.Pending)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(ct);
        foreach (var attempt in attempts)
        {
            if (gateways.Get(attempt.Provider) is not { } gateway)
            {
                continue;
            }

            PaymentStatus status;
            try
            {
                status = await gateway.GetStatusAsync(attempt.ProviderSessionId, ct);
            }
            catch (PaymentGatewayUnavailableException ex)
            {
                LogOutage(orderId, gateway.Provider, ex);
                return;
            }

            if (status != PaymentStatus.Succeeded)
            {
                continue;
            }

            var webhook = new WebhookEvent(
                FulfillOrderHandler.StatusEventId(attempt.ProviderSessionId), attempt.ProviderSessionId, WebhookEventType.Succeeded, attempt.Amount, time.GetUtcNow());
            db.ChangeTracker.Clear();
            var outcome = await fulfil.HandleAsync(attempt.Provider, webhook, ct);
            LogConfirmed(orderId, attempt.Id, outcome);
            return;
        }
    }

    [LoggerMessage(EventId = 8130, Level = LogLevel.Information, Message = "Order {OrderId}: the buyer's return confirmed payment attempt {AttemptId} ({Outcome}).")]
    private partial void LogConfirmed(Guid orderId, Guid attemptId, PaymentWebhookOutcome outcome);

    [LoggerMessage(EventId = 8131, Level = LogLevel.Warning, Message = "Order {OrderId}: could not ask {Provider} on the buyer's return; the webhook or the expiry sweep will decide.")]
    private partial void LogOutage(Guid orderId, string provider, Exception exception);
}
