using TicketRush.Domain.Events;

namespace TicketRush.Application.Features.Catalog;

/// <summary>A card in the catalog. <see cref="PriceFrom"/> is the cheapest tier (whole dong), null without tiers.</summary>
public sealed record EventSummary(
    Guid Id,
    string Slug,
    string Title,
    string Category,
    DateTimeOffset StartsAt,
    string VenueName,
    string City,
    string? CoverImageUrl,
    long? PriceFrom,
    DateTimeOffset? NextSaleOpensAt,
    bool IsOnSale,
    bool IsSoldOut);

public sealed record EventPage(IReadOnlyList<EventSummary> Items, string? NextCursor);

public sealed record VenueInfo(Guid Id, string Name, string Address, string City, string? MapUrl);

/// <summary>Remaining is deliberately absent: it is live data and comes from <c>/availability</c>.</summary>
public sealed record TierInfo(Guid Id, string Name, long Price, string Currency, TicketKind Kind, string Color);

/// <summary>Never carries the access-code hash; clients only learn that a code is required.</summary>
public sealed record PhaseInfo(
    Guid Id,
    string Name,
    DateTimeOffset OpensAt,
    DateTimeOffset ClosesAt,
    bool QueueEnabled,
    int MaxTicketsPerUser,
    bool RequiresAccessCode);

public sealed record EventDetail(
    Guid Id,
    string Slug,
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> Lineup,
    string? CoverImageUrl,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    EventStatus Status,
    bool IsSoldOut,
    VenueInfo Venue,
    IReadOnlyList<TierInfo> Tiers,
    IReadOnlyList<PhaseInfo> Phases);

public sealed record SeatMapSection(string Name, int SeatCount);

public sealed record SeatInfo(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId);

/// <summary>Static layout. <see cref="Version"/> is a content hash, used as the HTTP ETag.</summary>
public sealed record SeatMap(Guid EventId, string Version, IReadOnlyList<SeatMapSection> Sections, IReadOnlyList<SeatInfo> Seats);

public sealed record TierRemaining(Guid TierId, int Remaining);

/// <summary>Live availability; spec 07 fills <see cref="Unavailable"/> from Redis holds and sold tickets.</summary>
public sealed record EventAvailability(long Version, IReadOnlyList<int> Unavailable, IReadOnlyList<TierRemaining> Tiers);

public sealed record AvailabilityResponse(
    DateTimeOffset ServerNow,
    long Version,
    IReadOnlyList<int> Unavailable,
    IReadOnlyList<TierRemaining> Tiers);
