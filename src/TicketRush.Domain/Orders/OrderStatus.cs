namespace TicketRush.Domain.Orders;

/// <summary>
/// Order lifecycle (spec 08). <see cref="Paid"/>, <see cref="Refunded"/> and <see cref="Cancelled"/> are terminal
/// for payment events; <see cref="RefundPending"/> only ever moves to <see cref="Refunded"/>.
/// </summary>
public enum OrderStatus
{
    Pending,
    AwaitingPayment,
    PaymentFailed,
    Paid,
    Cancelled,
    Expired,
    RefundPending,
    Refunded,
}
