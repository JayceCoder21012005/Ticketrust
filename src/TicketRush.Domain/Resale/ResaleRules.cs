using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Resale;

/// <summary>The time rules of spec 14.</summary>
public static class ResaleRules
{
    /// <summary>Listing, transferring, reserving and accepting stop this long before the event starts.</summary>
    public static readonly TimeSpan Cutoff = TimeSpan.FromHours(2);

    /// <summary>A new listing is the waitlist's alone for this long (spec 13).</summary>
    public static readonly TimeSpan WaitlistWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long a buyer's reservation (and its order) lives: Redis <c>PX 600000</c>.</summary>
    public static readonly TimeSpan ReservationTtl = TimeSpan.FromMinutes(10);

    /// <summary>A pending transfer expires after this long.</summary>
    public static readonly TimeSpan TransferTtl = TimeSpan.FromHours(48);

    public static bool IsOpen(DateTimeOffset startsAt, DateTimeOffset now) => now < startsAt - Cutoff;
}

/// <summary>A ticket was put up for resale: the waitlist gets its window first (spec 13), then the public.</summary>
public sealed record ListingCreated(Guid ListingId, Guid EventId, Guid TierId, int? SeatIndex, Guid SellerId, DateTimeOffset WaitlistUntil)
    : IDomainEvent;

/// <summary>A ticket is offered to a friend: email the recipient the link to accept it.</summary>
public sealed record TransferCreated(Guid TransferId, Guid TicketId, Guid EventId, Guid FromUserId, string ToEmail, DateTimeOffset ExpiresAt)
    : IDomainEvent;
