using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Notifications;

/// <summary>
/// A user follows an event and is owed its reminders. Identity is the pair (<see cref="UserId"/>, <see cref="EventId"/>),
/// so a second follow is a unique violation, not a second row; the inherited <c>Id</c> is not stored.
/// </summary>
public sealed class EventFollow : Entity
{
    private EventFollow(Guid userId, Guid eventId, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        EventId = eventId;
        CreatedAt = now;
    }

    // Required by EF Core.
    private EventFollow()
    {
    }

    public Guid UserId { get; private set; }

    public Guid EventId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static EventFollow Create(Guid userId, Guid eventId, DateTimeOffset now)
    {
        if (userId == Guid.Empty || eventId == Guid.Empty)
        {
            throw new DomainException("follow.invalid", "A follow needs a user and an event.");
        }

        return new EventFollow(userId, eventId, now);
    }
}
