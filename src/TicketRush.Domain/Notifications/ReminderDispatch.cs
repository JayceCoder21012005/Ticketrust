using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Notifications;

/// <summary>
/// Record that a reminder was (or is being) sent: the primary key (user, event, <see cref="Kind"/>) means a reminder goes
/// out once however many scheduler instances tick. Only a row that was actually inserted should be saved, so
/// <see cref="ReminderDue"/> is raised once. The inherited <c>Id</c> is not stored; it only keys the outbox message.
/// </summary>
public sealed class ReminderDispatch : AggregateRoot
{
    private ReminderDispatch(Guid userId, Guid eventId, ReminderKind kind, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        EventId = eventId;
        Kind = kind;
        CreatedAt = now;
    }

    // Required by EF Core.
    private ReminderDispatch()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EventId { get; private set; }

    public ReminderKind Kind { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static ReminderDispatch Create(Guid userId, Guid eventId, ReminderKind kind, DateTimeOffset now)
    {
        if (userId == Guid.Empty || eventId == Guid.Empty)
        {
            throw new DomainException("reminder.invalid", "A reminder needs a user and an event.");
        }

        var dispatch = new ReminderDispatch(userId, eventId, kind, now);
        dispatch.Raise(new ReminderDue(userId, eventId, kind));
        return dispatch;
    }
}
