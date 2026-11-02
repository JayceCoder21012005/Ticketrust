using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

/// <summary>
/// Event aggregate. Status transitions follow a small state machine:
/// <c>Draft → Published → Ended</c> and <c>Draft/Published → Cancelled</c>.
/// Illegal transitions throw <see cref="DomainException"/>.
/// </summary>
public sealed class Event : AggregateRoot, IAuditable
{
    private readonly List<SalePhase> _phases = [];
    private readonly List<TicketTier> _tiers = [];

    private Event(
        Guid id,
        Guid organizerId,
        string slug,
        string title,
        string category,
        Guid venueId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
        : base(id)
    {
        OrganizerId = organizerId;
        Slug = slug;
        Title = title;
        Category = category;
        VenueId = venueId;
        StartsAt = startsAt;
        EndsAt = endsAt;
        Status = EventStatus.Draft;
    }

    private Event()
    {
    }

    public Guid OrganizerId { get; private set; }

    public string Slug { get; private set; } = null!;

    public string Title { get; private set; } = null!;

    public string Description { get; private set; } = string.Empty;

    public string Category { get; private set; } = null!;

    public IReadOnlyList<string> Lineup { get; private set; } = [];

    public string? CoverImageUrl { get; private set; }

    public Guid VenueId { get; private set; }

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public EventStatus Status { get; private set; }

    public bool IsSoldOut { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<SalePhase> Phases => _phases.AsReadOnly();

    public IReadOnlyList<TicketTier> Tiers => _tiers.AsReadOnly();

    public static Event CreateDraft(
        Guid organizerId,
        string slug,
        string title,
        string category,
        Guid venueId,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        EnsureValidSchedule(startsAt, endsAt);
        return new Event(Guid.CreateVersion7(), organizerId, slug, title.Trim(), category, venueId, startsAt, endsAt);
    }

    public void UpdateDetails(string description, IEnumerable<string> lineup, string? coverImageUrl)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft, EventStatus.Published);
        Description = description;
        Lineup = lineup.Select(artist => artist.Trim()).Where(artist => artist.Length > 0).ToList();
        CoverImageUrl = coverImageUrl;
    }

    public TicketTier AddTier(string name, Money price, TicketKind kind, int? capacity, string color)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);
        var tier = new TicketTier(Id, name, price, kind, capacity, color);
        _tiers.Add(tier);
        return tier;
    }

    public SalePhase AddPhase(
        string name,
        DateTimeOffset opensAt,
        DateTimeOffset closesAt,
        bool queueEnabled,
        int maxTicketsPerUser,
        string? accessCodeHash = null)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);

        if (closesAt > StartsAt)
        {
            throw new DomainException("phase.after_event_start", "A sale phase must close before the event starts.");
        }

        if (_phases.Exists(phase => phase.Overlaps(opensAt, closesAt)))
        {
            throw new DomainException("event.phase_overlap", "Sale phases of one event must not overlap.");
        }

        var phase = new SalePhase(Id, name, opensAt, closesAt, queueEnabled, maxTicketsPerUser, accessCodeHash);
        _phases.Add(phase);
        return phase;
    }

    /// <summary>Publishing is idempotent: publishing an already published event changes nothing.</summary>
    public void Publish()
    {
        if (Status == EventStatus.Published)
        {
            return;
        }

        EnsureStatus("event.not_publishable", EventStatus.Draft);
        Status = EventStatus.Published;
        Raise(new EventPublished(Id, OrganizerId));
    }

    public void Cancel()
    {
        EnsureStatus("event.not_cancellable", EventStatus.Draft, EventStatus.Published);
        Status = EventStatus.Cancelled;
        Raise(new EventCancelled(Id, OrganizerId));
    }

    public void MarkEnded()
    {
        EnsureStatus("event.not_endable", EventStatus.Published);
        Status = EventStatus.Ended;
        Raise(new EventEnded(Id));
    }

    public void MarkSoldOut(bool isSoldOut) => IsSoldOut = isSoldOut;

    private static void EnsureValidSchedule(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
        {
            throw new DomainException("event.invalid_schedule", "An event must end after it starts.");
        }
    }

    private void EnsureStatus(string code, params EventStatus[] allowed)
    {
        if (!allowed.Contains(Status))
        {
            throw new DomainException(code, $"Operation is not allowed while the event is {Status}.");
        }
    }
}
