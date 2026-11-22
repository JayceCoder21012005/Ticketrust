using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;

namespace TicketRush.Application.Features.Resale;

public static class ResaleErrors
{
    public static readonly Error NotAllowed =
        Error.Conflict("resale.not_allowed", "Only a valid ticket of a published event can be resold, until 2 hours before it starts.");

    public static readonly Error ListingUnavailable = Error.Conflict("resale.listing_unavailable", "This listing is no longer available.");

    public static readonly Error OwnListing = Error.Conflict("resale.own_listing", "You cannot buy your own listing.");

    public static readonly Error ListingNotFound = Error.NotFound("resale.listing_not_found", "There is no such listing.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden("resale.email_not_confirmed", "Confirm your email address before buying a ticket.");

    public static readonly Error LimitExceeded =
        Error.Conflict("resale.limit_exceeded", "You already have as many tickets for this event as one buyer may have.");

    public static Error PriceAboveCap(long faceValue) => Error.Validation(
        "resale.price_above_cap",
        "A resale price cannot be above the ticket's face value.",
        new Dictionary<string, string[]> { ["price"] = [$"At most {faceValue}."] });

    public static Error InvalidPrice => Error.Validation(
        "resale.invalid_price", "A resale price must be at least 1.", new Dictionary<string, string[]> { ["price"] = ["At least 1."] });
}

public static class TransferErrors
{
    public static readonly Error NotAllowed =
        Error.Conflict("transfer.not_allowed", "Only a valid ticket of a published event can be transferred, until 2 hours before it starts.");

    /// <summary>Also answered to anyone but the recipient, so transfer ids cannot be probed (403-as-404).</summary>
    public static readonly Error NotFound = Error.NotFound("transfer.not_found", "There is no such transfer.");

    public static Error Self => Error.Validation(
        "transfer.self", "You cannot transfer a ticket to yourself.", new Dictionary<string, string[]> { ["toEmail"] = ["That is your own email."] });

    public static Error InvalidEmail => Error.Validation(
        "transfer.invalid_email", "Enter the recipient's email address.", new Dictionary<string, string[]> { ["toEmail"] = ["Invalid email."] });
}

/// <param name="SeatLabel">"A · 1 · 6" style; null for general admission.</param>
public sealed record ListingResponse(
    Guid ListingId,
    Guid TicketId,
    Guid EventId,
    Guid TierId,
    string? SeatLabel,
    long Price,
    string Currency,
    ResaleListingStatus Status,
    DateTimeOffset? WaitlistUntil,
    DateTimeOffset CreatedAt);

public sealed record PublicListing(Guid ListingId, string? SeatLabel, long Price, string Currency);

/// <summary>One tier of the "Vé bán lại" section, cheapest first.</summary>
public sealed record ListingTierGroup(Guid TierId, string TierName, string Color, long FaceValue, IReadOnlyList<PublicListing> Listings);

public sealed record TransferResponse(
    Guid TransferId,
    Guid TicketId,
    Guid EventId,
    string ToEmail,
    TicketTransferStatus Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>The recipient's accept page: what they get and from whom. <see cref="NewTicketId"/> once accepted.</summary>
public sealed record TransferInvite(
    Guid TransferId,
    TicketTransferStatus Status,
    DateTimeOffset ExpiresAt,
    string FromName,
    Guid EventId,
    string EventSlug,
    string EventTitle,
    DateTimeOffset StartsAt,
    string Venue,
    string TierName,
    string? SeatLabel,
    Guid? NewTicketId);

internal static class SeatLabelText
{
    public static string? Of(Orders.SeatLabel? label) => label is null ? null : $"{label.Section} · {label.Row} · {label.Number}";
}
