namespace TicketRush.Domain.Notifications;

/// <summary>What an in-app notice is about. Stored as its name; later specs add a value per new trigger.</summary>
public enum NotificationType
{
    OrderPaid,
    RefundRequested,
    Refunded,
    OrderExpired,
    EventCancelled,
    SaleOpensSoon,
    EventStartsSoon,
    WaitlistOffer,
    OrganizerApplication,
    EventModeration,
}
