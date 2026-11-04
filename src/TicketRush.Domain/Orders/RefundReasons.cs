namespace TicketRush.Domain.Orders;

/// <summary>Values stored in <c>Order.RefundReason</c> and carried by <see cref="RefundRequested"/>.</summary>
public static class RefundReasons
{
    /// <summary>Payment succeeded after the hold died and the seats were gone (C3).</summary>
    public const string SeatsUnavailable = "seats_unavailable";

    /// <summary>The organizer cancelled the event after the order was paid.</summary>
    public const string EventCancelled = "event_cancelled";
}
