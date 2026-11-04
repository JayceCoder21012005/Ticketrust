using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;

namespace TicketRush.Application.Features.Organizer;

/// <param name="Shared">Seed venues every organizer may use but not edit.</param>
public sealed record VenueItem(Guid Id, string Name, string Address, string City, string? MapUrl, int Capacity, bool Shared);

public sealed record OrganizerEventItem(
    Guid Id,
    string Slug,
    string Title,
    EventStatus Status,
    DateTimeOffset StartsAt,
    string VenueName,
    int SeatCount,
    string? CoverImageUrl);

/// <param name="Price">Whole VND.</param>
public sealed record TierItem(Guid Id, string Name, long Price, TicketKind Kind, int? Capacity, string Color);

/// <param name="HasAccessCode">The code itself is stored only as a hash and is never returned.</param>
public sealed record PhaseItem(
    Guid Id,
    string Name,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    bool QueueEnabled,
    int MaxTicketsPerUser,
    bool HasAccessCode);

/// <param name="Version">Concurrency token; send it back with every change.</param>
public sealed record OrganizerEvent(
    Guid Id,
    string Slug,
    string Title,
    EventStatus Status,
    string Category,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Description,
    IReadOnlyList<string> Lineup,
    string? CoverImageUrl,
    IReadOnlyList<TierItem> Tiers,
    SeatLayoutDefinition? Layout,
    IReadOnlyDictionary<Guid, int> SeatsPerTier,
    IReadOnlyList<PhaseItem> Phases,
    string Version);
