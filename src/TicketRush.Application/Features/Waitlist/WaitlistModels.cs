using TicketRush.Application.Features.Holds;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Waitlist;

public static class WaitlistErrors
{
    public static readonly Error NotAvailable =
        Error.Conflict("waitlist.not_available", "The waitlist opens only when the event is sold out or its sale has closed.");

    public static readonly Error AlreadyJoined = Error.Conflict("waitlist.already_joined", "You are already on this event's waitlist.");

    public static readonly Error LimitExceeded =
        Error.Conflict("waitlist.limit_exceeded", "You already have as many tickets for this event as one buyer may have.");

    public static readonly Error OfferExpired = Error.Conflict("waitlist.offer_expired", "This offer has expired.");

    public static readonly Error NotJoined = Error.NotFound("waitlist.not_joined", "You are not on this event's waitlist.");

    public static readonly Error OfferNotFound = Error.NotFound("waitlist.offer_not_found", "There is no such offer.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden("waitlist.email_not_confirmed", "Confirm your email address before joining a waitlist.");

    public static Error InvalidTier => Error.Validation(
        "waitlist.invalid_tier", "That tier does not belong to this event.", new Dictionary<string, string[]> { ["tierId"] = ["Unknown tier."] });

    public static Error InvalidQuantity => Error.Validation(
        "waitlist.invalid_quantity",
        $"Ask for {WaitlistEntry.MinQuantity} to {WaitlistEntry.MaxQuantity} tickets.",
        new Dictionary<string, string[]> { ["quantity"] = [$"Between {WaitlistEntry.MinQuantity} and {WaitlistEntry.MaxQuantity}."] });
}

/// <param name="TierId">Null for any tier.</param>
public sealed record JoinWaitlistCommand(Guid UserId, bool EmailVerified, Guid EventId, Guid? TierId, int Quantity);

/// <summary>The live offer of an entry.</summary>
public sealed record WaitlistOfferSummary(Guid OfferId, DateTimeOffset ExpiresAt);

/// <param name="Position">1 for the next in line; null unless <see cref="WaitlistStatus.Waiting"/>.</param>
public sealed record WaitlistStatusResponse(
    Guid EntryId,
    Guid EventId,
    WaitlistStatus Status,
    int? Position,
    Guid? TierId,
    int Quantity,
    DateTimeOffset JoinedAt,
    WaitlistOfferSummary? Offer);

/// <summary>An entry of <c>/account/waitlist</c> with what the list shows of its event.</summary>
public sealed record MyWaitlistItem(
    WaitlistStatusResponse Entry,
    string EventSlug,
    string EventTitle,
    DateTimeOffset StartsAt,
    string City,
    string? CoverImageUrl,
    string? TierName);

/// <summary>The offer page: the held tickets, priced, and when they go to the next in line.</summary>
public sealed record WaitlistOfferResponse(
    Guid OfferId,
    Guid EventId,
    string EventSlug,
    string EventTitle,
    DateTimeOffset StartsAt,
    DateTimeOffset ExpiresAt,
    HoldResponse Hold);

/// <summary>What checkout needs to continue from the offered hold (spec 08 works from the user's hold).</summary>
public sealed record AcceptedWaitlistOffer(Guid HoldId, Guid EventId, string EventSlug, DateTimeOffset ExpiresAt);
