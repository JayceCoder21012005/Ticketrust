using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Notifications;

/// <summary>
/// A follower is owed a reminder (<see cref="ReminderDispatch"/> was just inserted, so this is raised at most once per
/// user, event and kind). Consumer: the reminder handler writes the in-app notice and its email.
/// </summary>
public sealed record ReminderDue(Guid UserId, Guid EventId, ReminderKind Kind) : IDomainEvent;
