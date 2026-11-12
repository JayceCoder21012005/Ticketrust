namespace TicketRush.Domain.Notifications;

/// <summary>Which reminder a follower gets for an event: the sale opens within 30 minutes, or the event starts within 24 hours.</summary>
public enum ReminderKind
{
    SaleOpens30m,
    EventStarts24h,
}
