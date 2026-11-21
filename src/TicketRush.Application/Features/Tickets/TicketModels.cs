using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Tickets;

public sealed record TicketSeat(string Section, string Row, string Number);

/// <summary>One ticket as its owner sees it. The QR secret is never part of it (only <c>qr-key</c> returns it).</summary>
/// <param name="Seat">Null for general admission.</param>
/// <param name="HolderName">The owner's full name.</param>
public sealed record TicketItem(Guid Id, Guid OrderId, string TierName, TicketSeat? Seat, TicketStatus Status, string HolderName);

public sealed record TicketEventGroup(
    Guid EventId,
    string Slug,
    string Title,
    string? CoverImageUrl,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string VenueName,
    string City,
    EventStatus Status,
    IReadOnlyList<TicketItem> Tickets);

/// <summary><c>GET /me/tickets</c>: the owner's tickets grouped by event, capped at <see cref="ListMyTickets.ListMyTicketsHandler.MaxTickets"/> tickets.</summary>
public sealed record MyTicketsResponse(IReadOnlyList<TicketEventGroup> Events);

public sealed record TicketEventSummary(
    Guid EventId,
    string Slug,
    string Title,
    string? CoverImageUrl,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string VenueName,
    string City,
    EventStatus Status);

/// <param name="OrderTicketIds">Every ticket of the same order the caller owns, in a stable order (for sibling navigation).</param>
public sealed record TicketDetail(
    Guid Id,
    Guid OrderId,
    string TierName,
    TicketSeat? Seat,
    TicketStatus Status,
    string HolderName,
    TicketEventSummary Event,
    IReadOnlyList<Guid> OrderTicketIds,
    TicketResale Resale);

/// <summary>What the wallet's "…" menu needs (spec 14).</summary>
/// <param name="FaceValue">The most a resale may ask (the tier price).</param>
/// <param name="CanChange">The ticket is Valid and its event is published and more than 2 hours away: it may be listed or transferred.</param>
/// <param name="Listing">The live listing (Active or Reserved) while the ticket is Listed for resale.</param>
/// <param name="Transfer">The pending transfer while the ticket is Listed for a friend.</param>
public sealed record TicketResale(long FaceValue, string Currency, bool CanChange, TicketListingState? Listing, TicketTransferState? Transfer);

public sealed record TicketListingState(Guid ListingId, long Price, TicketRush.Domain.Resale.ResaleListingStatus Status, DateTimeOffset? WaitlistUntil);

public sealed record TicketTransferState(Guid TransferId, string ToEmail, DateTimeOffset ExpiresAt);

/// <param name="Secret">Standard base64 of the 32 secret bytes.</param>
/// <param name="StepSeconds">How long a code stays the same.</param>
/// <param name="Format">The code format tag (<c>TR1</c>).</param>
public sealed record QrKeyResponse(Guid TicketId, string Secret, int StepSeconds, string Format);
