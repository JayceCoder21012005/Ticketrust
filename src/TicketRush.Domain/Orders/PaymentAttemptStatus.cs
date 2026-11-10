namespace TicketRush.Domain.Orders;

public enum PaymentAttemptStatus
{
    Pending,
    Succeeded,
    Failed,
    Expired,

    /// <summary>Money reached this session but cannot pay for the order (a second payment, a closed order): it goes back.</summary>
    RefundPending,

    /// <summary>The gateway confirmed the refund of this session's payment.</summary>
    Refunded,
}
