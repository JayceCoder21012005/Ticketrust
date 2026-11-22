using NSubstitute;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Application.Features.Observability;
using TicketRush.Domain.Orders;

namespace TicketRush.Application.Tests.Observability;

public sealed class SalesMetricHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ISalesMetrics _metrics = Substitute.For<ISalesMetrics>();

    private static IntegrationEventContext Context() => new(Guid.NewGuid(), DateTimeOffset.UtcNow, 1);

    [Fact]
    public async Task A_paid_order_and_refunds_of_orders_and_sessions_are_counted_with_their_reason()
    {
        await new OrderPaidMetricHandler(_metrics).HandleAsync(
            new OrderPaid(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "VND", 1, DateTimeOffset.UtcNow, []), Context(), Ct);
        await new RefundRequestedMetricHandler(_metrics).HandleAsync(
            new RefundRequested(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, "VND", RefundReasons.EventCancelled), Context(), Ct);
        await new PaymentRefundRequestedMetricHandler(_metrics).HandleAsync(
            new PaymentRefundRequested(Guid.NewGuid(), Guid.NewGuid(), "mock", "s1", 1, "VND", RefundReasons.DuplicatePayment), Context(), Ct);

        _metrics.Received(1).OrderPaid();
        _metrics.Received(1).RefundRequested(RefundReasons.EventCancelled);
        _metrics.Received(1).RefundRequested(RefundReasons.DuplicatePayment);
    }
}
