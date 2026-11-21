using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Waitlist;

public enum WaitlistStatus
{
    Waiting,
    Offered,
    Converted,
    Expired,
    Left,

    /// <summary>The event was cancelled: nothing will come back to offer.</summary>
    Closed,
}

/// <summary>A held ticket was offered to the entry's user (spec 13): sent as an in-app notification and an email.</summary>
/// <remarks><see cref="HoldId"/> is empty for a resale listing offer (spec 14), which names <see cref="ListingId"/> instead.</remarks>
public sealed record WaitlistOfferIssued(Guid EntryId, Guid EventId, Guid UserId, Guid HoldId, DateTimeOffset ExpiresAt, Guid? ListingId = null) : IDomainEvent;

/// <summary>
/// A fan waiting for a sold-out event (spec 13): FIFO by <see cref="QueuedAt"/>. Waiting → Offered → Converted when the order is
/// paid; an unused offer sends the entry to the back once (<see cref="Miss"/>), a second miss expires it. One live entry per
/// (event, user), enforced by a unique partial index.
/// </summary>
public sealed class WaitlistEntry : AggregateRoot
{
    public const int MinQuantity = 1;
    public const int MaxQuantity = 4;

    /// <summary>Unused offers an entry may miss before it expires (the first one sends it to the back).</summary>
    public const int MissesBeforeExpiry = 2;

    private WaitlistEntry(Guid eventId, Guid userId, Guid? tierId, int quantity, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        EventId = eventId;
        UserId = userId;
        TierId = tierId;
        Quantity = quantity;
        CreatedAt = now;
        QueuedAt = now;
        Status = WaitlistStatus.Waiting;
    }

    // Required by EF Core.
    private WaitlistEntry()
    {
    }

    public Guid EventId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>A specific tier, or null for any tier.</summary>
    public Guid? TierId { get; private set; }

    public int Quantity { get; private set; }

    public WaitlistStatus Status { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The FIFO key: when the entry (re)joined the line.</summary>
    public DateTimeOffset QueuedAt { get; private set; }

    public int Misses { get; private set; }

    /// <summary>The hold made for the current offer; null unless <see cref="WaitlistStatus.Offered"/>.</summary>
    public Guid? OfferHoldId { get; private set; }

    /// <summary>The resale listing of the current offer (spec 14); null unless offered a listing.</summary>
    public Guid? OfferListingId { get; private set; }

    public DateTimeOffset? OfferExpiresAt { get; private set; }

    public bool IsLive => Status is WaitlistStatus.Waiting or WaitlistStatus.Offered;

    public static WaitlistEntry Join(Guid eventId, Guid userId, Guid? tierId, int quantity, DateTimeOffset now)
    {
        if (eventId == Guid.Empty || userId == Guid.Empty)
        {
            throw new DomainException("waitlist.invalid_reference", "A waitlist entry needs an event and a user.");
        }

        if (quantity is < MinQuantity or > MaxQuantity)
        {
            throw new DomainException("waitlist.invalid_quantity", $"Ask for {MinQuantity} to {MaxQuantity} tickets.");
        }

        return new WaitlistEntry(eventId, userId, tierId, quantity, now);
    }

    /// <summary>Whether this entry takes <paramref name="units"/> returned tickets of <paramref name="tierId"/>.</summary>
    public bool Wants(Guid tierId, int units) => (TierId is null || TierId == tierId) && Quantity <= units;

    public void Offer(Guid holdId, DateTimeOffset expiresAt)
    {
        Require(WaitlistStatus.Waiting, nameof(Offer));
        Status = WaitlistStatus.Offered;
        OfferHoldId = holdId;
        OfferExpiresAt = expiresAt;
        Raise(new WaitlistOfferIssued(Id, EventId, UserId, holdId, expiresAt));
    }

    /// <summary>A new resale listing is this entry's alone until <paramref name="expiresAt"/> (spec 14, the waitlist window).</summary>
    public void OfferListing(Guid listingId, DateTimeOffset expiresAt)
    {
        Require(WaitlistStatus.Waiting, nameof(OfferListing));
        Status = WaitlistStatus.Offered;
        OfferListingId = listingId;
        OfferExpiresAt = expiresAt;
        Raise(new WaitlistOfferIssued(Id, EventId, UserId, Guid.Empty, expiresAt, listingId));
    }

    /// <summary>The offered hold was paid for.</summary>
    public void Convert()
    {
        Require(WaitlistStatus.Offered, nameof(Convert));
        Status = WaitlistStatus.Converted;
    }

    /// <summary>The offer ran out unused: back to the end of the line once, expired the second time.</summary>
    public void Miss(DateTimeOffset now)
    {
        Require(WaitlistStatus.Offered, nameof(Miss));
        Misses++;
        OfferHoldId = null;
        OfferListingId = null;
        OfferExpiresAt = null;
        if (Misses >= MissesBeforeExpiry)
        {
            Status = WaitlistStatus.Expired;
            return;
        }

        Status = WaitlistStatus.Waiting;
        QueuedAt = now;
    }

    public void Leave()
    {
        if (!IsLive)
        {
            throw new DomainException("waitlist.illegal_transition", $"Cannot leave a waitlist entry that is {Status}.");
        }

        Status = WaitlistStatus.Left;
        OfferHoldId = null;
        OfferListingId = null;
        OfferExpiresAt = null;
    }

    /// <summary>The event was cancelled (its holds are released by the cancellation itself).</summary>
    public void Close()
    {
        if (!IsLive)
        {
            throw new DomainException("waitlist.illegal_transition", $"Cannot close a waitlist entry that is {Status}.");
        }

        Status = WaitlistStatus.Closed;
        OfferHoldId = null;
        OfferListingId = null;
        OfferExpiresAt = null;
    }

    private void Require(WaitlistStatus from, string method)
    {
        if (Status != from)
        {
            throw new DomainException("waitlist.illegal_transition", $"Cannot {method} a waitlist entry that is {Status}.");
        }
    }
}
