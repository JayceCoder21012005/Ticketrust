using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Orders;

internal static class OrderTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset ExpiresAt = Now.AddMinutes(10);
    public static readonly Guid UserId = Guid.CreateVersion7();
    public static readonly Guid EventId = Guid.CreateVersion7();
    public static readonly Guid HoldId = Guid.CreateVersion7();
    public static readonly Guid TierId = Guid.CreateVersion7();
    public static readonly BuyerInfo Buyer = new("Nguyen Van A", "a@example.com", "0901234567");

    public static IReadOnlyList<OrderItem> SeatedItems() =>
    [
        new OrderItem(TierId, 12, new Money(800_000), 1),
        new OrderItem(TierId, 13, new Money(800_000), 1),
    ];

    public static Order Pending() =>
        Order.Create(UserId, EventId, HoldId, SeatedItems(), new Money(1_600_000), Buyer, ExpiresAt, Now);

    /// <summary>Builds an order in the requested status through legal transitions only, with no pending events.</summary>
    public static Order InStatus(OrderStatus status)
    {
        var order = Pending();
        switch (status)
        {
            case OrderStatus.Pending:
                break;
            case OrderStatus.AwaitingPayment:
                order.StartPayment(Now);
                break;
            case OrderStatus.PaymentFailed:
                order.StartPayment(Now);
                order.MarkPaymentFailed();
                break;
            case OrderStatus.Paid:
                order.StartPayment(Now);
                order.MarkPaid(Now);
                break;
            case OrderStatus.Cancelled:
                order.Cancel();
                break;
            case OrderStatus.Expired:
                order.Expire();
                break;
            case OrderStatus.RefundPending:
                order.StartPayment(Now);
                order.MarkPaid(Now);
                order.RequestRefundForCancelledEvent();
                break;
            case OrderStatus.Refunded:
                order.StartPayment(Now);
                order.MarkPaid(Now);
                order.RequestRefundForCancelledEvent();
                order.MarkRefunded();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        order.DequeueDomainEvents();
        return order;
    }
}
