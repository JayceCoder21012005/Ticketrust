using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications;

public static class NotificationErrors
{
    /// <summary>Also returned for another user's notification, so ids cannot be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("notification.not_found", "Notification not found.");
}
