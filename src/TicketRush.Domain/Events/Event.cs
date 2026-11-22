using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Domain.Events;

/// <summary>
/// Event aggregate. Status transitions follow a small state machine:
/// <c>Draft → Published → Ended</c> and <c>Draft/Published → Cancelled</c>.
/// Illegal transitions throw <see cref="DomainException"/>.
/// </summary>
[TicketRush.Domain.Audit.Audited]
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
        string city,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
        : base(id)
    {
        OrganizerId = organizerId;
        Slug = slug;
        Title = title;
        Category = category;
        VenueId = venueId;
        City = city;
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
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

    /// <summary>Copied from the venue: the search vector and the city filter must not need a join.</summary>
    public string City { get; private set; } = null!;

    public DateTimeOffset StartsAt { get; private set; }

    public DateTimeOffset EndsAt { get; private set; }

    public EventStatus Status { get; private set; }

    public bool IsSoldOut { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The section definitions the seats were generated from, kept so the organizer can edit them again.</summary>
    public SeatLayoutDefinition? Layout { get; private set; }

    public IReadOnlyList<SalePhase> Phases => _phases.AsReadOnly();

    public IReadOnlyList<TicketTier> Tiers => _tiers.AsReadOnly();

    public static Event CreateDraft(
        Guid organizerId,
        string slug,
        string title,
        string category,
        Venue venue,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt)
    {
        EnsureValidSchedule(startsAt, endsAt);
        EnsureKnownCategory(category);

        return new Event(Guid.CreateVersion7(), organizerId, slug, title.Trim(), category, venue.Id, venue.City, startsAt, endsAt);
    }

    public const int MinTitleLength = 3;
    public const int MaxTitleLength = 200;

    /// <summary>
    /// Text and cover stay editable after publishing (spec 05). Changing them on a published event raises
    /// <see cref="EventDetailsChanged"/> so public caches refresh.
    /// </summary>
    public void UpdateInfo(string title, string description, IEnumerable<string> lineup, string? coverImageUrl)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft, EventStatus.Published);

        var trimmed = title.Trim();
        if (trimmed.Length is < MinTitleLength or > MaxTitleLength)
        {
            throw new DomainException("event.invalid_title", $"Title must be {MinTitleLength}–{MaxTitleLength} characters.");
        }

        var artists = lineup.Select(artist => artist.Trim()).Where(artist => artist.Length > 0).ToList();
        var changed = trimmed != Title || description != Description || !artists.SequenceEqual(Lineup) || coverImageUrl != CoverImageUrl;

        Title = trimmed;
        Description = description;
        Lineup = artists;
        CoverImageUrl = coverImageUrl;

        if (changed && Status == EventStatus.Published)
        {
            Raise(new EventDetailsChanged(Id));
        }
    }

    public void UpdateSchedule(string category, Venue venue, DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);
        EnsureValidSchedule(startsAt, endsAt);
        EnsureKnownCategory(category);
        if (_phases.Exists(phase => phase.ClosesAt > startsAt))
        {
            throw new DomainException("phase.after_event_start", "Sale phases must close before the event starts; move them first.");
        }

        Category = category;
        VenueId = venue.Id;
        City = venue.City;
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
    }

    /// <summary>Replaces the tier list: entries with an id update that tier in place, the others are new; missing tiers go.</summary>
    public void ReplaceTiers(IReadOnlyList<TierDraft> tiers)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);

        foreach (var draft in tiers.Where(t => t.Id is not null))
        {
            if (!_tiers.Exists(t => t.Id == draft.Id))
            {
                throw new DomainException("tier.not_found", $"Tier {draft.Id} does not belong to this event.");
            }
        }

        var kept = tiers.Where(t => t.Id is not null).Select(t => t.Id!.Value).ToHashSet();
        var orphaned = Layout?.Sections.Where(s => !kept.Contains(s.TierId)).Select(s => s.Code).ToList() ?? [];
        if (orphaned.Count > 0)
        {
            throw new DomainException("tier.in_use", $"Tiers still used by sections {string.Join(", ", orphaned)} can't be removed.");
        }

        // A section's seats (or standing area) follow its tier's kind, so that kind can't change under it.
        var kinds = tiers.Where(t => t.Id is not null).ToDictionary(t => t.Id!.Value, t => t.Kind);
        var rekinded = Layout?.Sections
            .Where(s => _tiers.Single(t => t.Id == s.TierId).Kind != kinds[s.TierId])
            .Select(s => s.Code)
            .ToList() ?? [];
        if (rekinded.Count > 0)
        {
            throw new DomainException("tier.in_use", $"Tiers used by sections {string.Join(", ", rekinded)} can't change kind.");
        }

        _tiers.RemoveAll(t => !kept.Contains(t.Id));

        foreach (var draft in tiers)
        {
            if (draft.Id is { } id)
            {
                _tiers.Single(t => t.Id == id).Update(draft.Name, draft.Price, draft.Kind, draft.Capacity, draft.Color);
            }
            else
            {
                _tiers.Add(new TicketTier(Id, draft.Name, draft.Price, draft.Kind, draft.Capacity, draft.Color));
            }
        }
    }

    /// <summary>
    /// Validates <paramref name="definition"/> against this event's tiers, keeps it, and returns the seats to store.
    /// Handlers validate first (<see cref="SeatLayoutGenerator.Validate"/>) to report errors; this guards the invariant.
    /// </summary>
    public IReadOnlyList<GeneratedSeat> ReplaceLayout(SeatLayoutDefinition definition)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);

        var errors = SeatLayoutGenerator.Validate(definition, _tiers.ToDictionary(t => t.Id, t => t.Kind));
        if (errors.Count > 0)
        {
            throw new DomainException("layout.invalid", string.Join("; ", errors.Select(e => $"{e.Section}.{e.Field}: {e.Message}")));
        }

        Layout = definition;
        return SeatLayoutGenerator.Generate(definition);
    }

    /// <summary>
    /// Replaces the sale phases. Ids are kept when given; <see cref="PhaseDraft.KeepExistingCode"/> carries the old
    /// presale hash over, because the plain code is never stored and can't be re-sent.
    /// </summary>
    public void ReplacePhases(IReadOnlyList<PhaseDraft> phases)
    {
        EnsureStatus("event.not_editable", EventStatus.Draft);

        var replacement = new List<SalePhase>(phases.Count);
        foreach (var draft in phases)
        {
            EnsurePhaseFits(draft.OpensAt, draft.ClosesAt, replacement);
            var hash = draft.KeepExistingCode && draft.Id is { } id
                ? _phases.Find(p => p.Id == id)?.AccessCodeHash
                : draft.AccessCodeHash;
            replacement.Add(new SalePhase(
                Id, draft.Name, draft.OpensAt, draft.ClosesAt, draft.QueueEnabled, draft.MaxTicketsPerUser, hash, draft.Id));
        }

        _phases.Clear();
        _phases.AddRange(replacement);
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

        EnsurePhaseFits(opensAt, closesAt, _phases);
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

    /// <summary>Published → Suspended by an admin (spec 16 moderation). The organizer is told why.</summary>
    public void Suspend(string reason)
    {
        EnsureStatus("event.not_suspendable", EventStatus.Published);
        Status = EventStatus.Suspended;
        Raise(new EventSuspended(Id, OrganizerId, reason));
    }

    /// <summary>Suspended → Published again by an admin.</summary>
    public void Restore(string reason)
    {
        EnsureStatus("event.not_restorable", EventStatus.Suspended);
        Status = EventStatus.Published;
        Raise(new EventRestored(Id, OrganizerId, reason));
    }

    public void Cancel()
    {
        EnsureStatus("event.not_cancellable", EventStatus.Draft, EventStatus.Published, EventStatus.Suspended);
        Status = EventStatus.Cancelled;
        Raise(new EventCancelled(Id, OrganizerId));
    }

    public void MarkEnded()
    {
        EnsureStatus("event.not_endable", EventStatus.Published, EventStatus.Suspended);
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

    private static void EnsureKnownCategory(string category)
    {
        if (!EventCategories.IsKnown(category))
        {
            throw new DomainException("event.invalid_category", $"Unknown event category '{category}'.");
        }
    }

    private void EnsurePhaseFits(DateTimeOffset opensAt, DateTimeOffset closesAt, List<SalePhase> others)
    {
        if (closesAt > StartsAt)
        {
            throw new DomainException("phase.after_event_start", "A sale phase must close before the event starts.");
        }

        if (others.Exists(phase => phase.Overlaps(opensAt, closesAt)))
        {
            throw new DomainException("event.phase_overlap", "Sale phases of one event must not overlap.");
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
