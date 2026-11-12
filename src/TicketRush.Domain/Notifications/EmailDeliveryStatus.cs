namespace TicketRush.Domain.Notifications;

/// <summary>Where an <see cref="EmailDelivery"/> stands. Only <see cref="Pending"/> rows are worked on.</summary>
public enum EmailDeliveryStatus
{
    Pending,
    Sent,
    DeadLettered,
}
