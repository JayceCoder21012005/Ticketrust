using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

/// <summary>A sale window of an event. Owned by <see cref="Event"/>; only created through it.</summary>
public sealed class SalePhase : Entity
{
    public const int MinTicketsPerUser = 1;
    public const int MaxTicketsPerUserLimit = 8;

    internal SalePhase(
        Guid eventId,
        string name,
        DateTimeOffset opensAt,
        DateTimeOffset closesAt,
        bool queueEnabled,
        int maxTicketsPerUser,
        string? accessCodeHash)
        : base(Guid.CreateVersion7())
    {
        if (closesAt <= opensAt)
        {
            throw new DomainException("phase.invalid_window", "A sale phase must close after it opens.");
        }

        if (maxTicketsPerUser is < MinTicketsPerUser or > MaxTicketsPerUserLimit)
        {
            throw new DomainException("phase.invalid_limit", "Max tickets per user must be between 1 and 8.");
        }

        EventId = eventId;
        Name = name.Trim();
        OpensAt = opensAt;
        ClosesAt = closesAt;
        QueueEnabled = queueEnabled;
        MaxTicketsPerUser = maxTicketsPerUser;
        AccessCodeHash = accessCodeHash;
    }

    private SalePhase()
    {
    }

    public Guid EventId { get; private set; }

    public string Name { get; private set; } = null!;

    public DateTimeOffset OpensAt { get; private set; }

    public DateTimeOffset ClosesAt { get; private set; }

    public bool QueueEnabled { get; private set; }

    public string? AccessCodeHash { get; private set; }

    public int MaxTicketsPerUser { get; private set; }

    public bool Overlaps(DateTimeOffset opensAt, DateTimeOffset closesAt) =>
        opensAt < ClosesAt && OpensAt < closesAt;
}
