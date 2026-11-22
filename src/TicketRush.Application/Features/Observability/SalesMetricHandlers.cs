using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Domain.Orders;

namespace TicketRush.Application.Features.Observability;

/// <summary>
/// Counts paid orders and refunds from the outbox (spec 17): each handler runs at most once per message, so a retried
/// transaction or a redelivered webhook never counts twice.
/// </summary>
public sealed class OrderPaidMetricHandler(ISalesMetrics metrics) : IIntegrationEventHandler<OrderPaid>
{
    public Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        metrics.OrderPaid();
        return Task.CompletedTask;
    }
}

/// <summary>An order that cannot be kept (event cancelled, seats lost, admin refund…).</summary>
public sealed class RefundRequestedMetricHandler(ISalesMetrics metrics) : IIntegrationEventHandler<RefundRequested>
{
    public Task HandleAsync(RefundRequested message, IntegrationEventContext context, CancellationToken ct)
    {
        metrics.RefundRequested(message.Reason);
        return Task.CompletedTask;
    }
}

/// <summary>One payment session refunded while the order stands (a duplicate payment, a mismatched amount, a closed order).</summary>
public sealed class PaymentRefundRequestedMetricHandler(ISalesMetrics metrics) : IIntegrationEventHandler<PaymentRefundRequested>
{
    public Task HandleAsync(PaymentRefundRequested message, IntegrationEventContext context, CancellationToken ct)
    {
        metrics.RefundRequested(message.Reason);
        return Task.CompletedTask;
    }
}
