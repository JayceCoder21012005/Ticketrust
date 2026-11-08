namespace TicketRush.Domain.Orders;

/// <summary>Values stored in <c>Order.RefundReason</c> and carried by <see cref="RefundRequested"/>.</summary>
public static class RefundReasons
{
    /// <summary>Payment succeeded after the hold died and the seats were gone (C3).</summary>
    public const string SeatsUnavailable = "seats_unavailable";

    /// <summary>The organizer cancelled the event (after the order was paid, or before a late payment arrived).</summary>
    public const string EventCancelled = "event_cancelled";

    /// <summary>The payment arrived after the event had ended.</summary>
    public const string EventEnded = "event_ended";

    /// <summary>The tickets would take the buyer over the event's per-user limit.</summary>
    public const string PurchaseLimitExceeded = "purchase_limit_exceeded";

    /// <summary>
    /// A second session paid for an order another session already paid (or that is already being refunded); only that
    /// session's money goes back (<see cref="PaymentRefundRequested"/>), the order and its tickets stay as they are.
    /// </summary>
    public const string DuplicatePayment = "duplicate_payment";

    /// <summary>Money arrived for an order that can no longer be paid (cancelled, or never started payment).</summary>
    public const string OrderClosed = "order_closed";

    /// <summary>The provider reports a different amount than the session was opened for: nothing is issued, the money goes back.</summary>
    public const string AmountMismatch = "amount_mismatch";
}
