using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Resale;

public enum ResaleListingStatus
{
    Active,
    Reserved,
    Sold,
    Cancelled,
}

/// <summary>
/// A ticket put up for resale at most at face value (spec 14). <c>Active → Reserved → Sold</c>, <c>Reserved → Active</c> when the
/// buyer's reservation ends, <c>Active → Cancelled</c>. For <see cref="ResaleRules.WaitlistWindow"/> after it is created only the
/// waiter it was offered to may reserve it (13, approach A). The row's <c>xmin</c> makes concurrent reservations lose cleanly.
/// </summary>
public sealed class ResaleListing : AggregateRoot, IAuditable
{
    private ResaleListing(Ticket ticket, Money price, DateTimeOffset now)
        : base(Guid.CreateVersion7())
    {
        TicketId = ticket.Id;
        SellerId = ticket.OwnerUserId;
        EventId = ticket.EventId;
        TierId = ticket.TierId;
        SeatIndex = ticket.SeatIndex;
        Price = price;
        Status = ResaleListingStatus.Active;
        WaitlistUntil = now + ResaleRules.WaitlistWindow;
    }

    // Required by EF Core.
    private ResaleListing()
    {
        Price = Money.Zero();
    }

    public Guid TicketId { get; private set; }

    public Guid SellerId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid TierId { get; private set; }

    public int? SeatIndex { get; private set; }

    public Money Price { get; private set; }

    public ResaleListingStatus Status { get; private set; }

    /// <summary>Until then the listing is the waitlist's (hidden from the public). Null once it is public.</summary>
    public DateTimeOffset? WaitlistUntil { get; private set; }

    /// <summary>The waiter the listing was offered to during the window.</summary>
    public Guid? OfferedToUserId { get; private set; }

    /// <summary>The resale order that reserved (or bought) it.</summary>
    public Guid? ReservedOrderId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Lists <paramref name="ticket"/> (Valid → Listed) at <paramref name="price"/>, 1 to <paramref name="faceValue"/>.</summary>
    public static ResaleListing Create(Ticket ticket, Money price, Money faceValue, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        ArgumentNullException.ThrowIfNull(price);
        ArgumentNullException.ThrowIfNull(faceValue);
        if (ticket.Status != TicketStatus.Valid)
        {
            throw new DomainException("resale.not_allowed", $"A ticket that is {ticket.Status} cannot be resold.");
        }

        if (price.Amount < 1)
        {
            throw new DomainException("resale.invalid_price", "A resale price must be at least 1.");
        }

        if (price.Currency != faceValue.Currency || price.Amount > faceValue.Amount)
        {
            throw new DomainException("resale.price_above_cap", "A resale price cannot be above the ticket's face value.");
        }

        ticket.List();
        var listing = new ResaleListing(ticket, price, now);
        listing.Raise(new ListingCreated(listing.Id, listing.EventId, listing.TierId, listing.SeatIndex, listing.SellerId, listing.WaitlistUntil!.Value));
        return listing;
    }

    /// <summary>Whether <paramref name="userId"/> may reserve it now: active, not theirs, and past the window unless it was offered to them.</summary>
    public bool IsOpenTo(Guid userId, DateTimeOffset now) =>
        Status == ResaleListingStatus.Active && userId != SellerId
        && (WaitlistUntil is null || now >= WaitlistUntil || OfferedToUserId == userId);

    /// <summary>The waitlist handed its window to <paramref name="userId"/>.</summary>
    public void OfferTo(Guid userId) => OfferedToUserId = userId;

    /// <summary>Nobody waits (or the waiter gave up): the public sees it at once.</summary>
    public void OpenToPublic()
    {
        WaitlistUntil = null;
        OfferedToUserId = null;
    }

    public void Reserve(Guid orderId, Guid buyerId, DateTimeOffset now)
    {
        if (buyerId == SellerId)
        {
            throw new DomainException("resale.own_listing", "You cannot buy your own listing.");
        }

        if (!IsOpenTo(buyerId, now))
        {
            throw Unavailable();
        }

        Status = ResaleListingStatus.Reserved;
        ReservedOrderId = orderId;
    }

    /// <returns>False when the listing is not reserved by <paramref name="orderId"/> (someone else's, sold, cancelled).</returns>
    public bool ReleaseReservation(Guid orderId)
    {
        if (Status != ResaleListingStatus.Reserved || ReservedOrderId != orderId)
        {
            return false;
        }

        Status = ResaleListingStatus.Active;
        ReservedOrderId = null;
        return true;
    }

    /// <summary>Paid: reserved by <paramref name="orderId"/>, or still active (a late payment after the reservation ran out).</summary>
    public void MarkSold(Guid orderId)
    {
        var mine = Status == ResaleListingStatus.Reserved && ReservedOrderId == orderId;
        if (!mine && Status != ResaleListingStatus.Active)
        {
            throw Unavailable();
        }

        Status = ResaleListingStatus.Sold;
        ReservedOrderId = orderId;
    }

    /// <summary>The seller withdraws it (Active only): the ticket is theirs again.</summary>
    public void Cancel(Ticket ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (Status != ResaleListingStatus.Active)
        {
            throw Unavailable();
        }

        if (ticket.Id != TicketId)
        {
            throw new DomainException("resale.invalid_reference", "That is not the listed ticket.");
        }

        ticket.Unlist();
        Status = ResaleListingStatus.Cancelled;
    }

    /// <summary>The event was cancelled: its tickets are voided by the cancellation itself.</summary>
    public void CloseForCancelledEvent()
    {
        if (Status is ResaleListingStatus.Active or ResaleListingStatus.Reserved)
        {
            Status = ResaleListingStatus.Cancelled;
        }
    }

    private static DomainException Unavailable() => new("resale.listing_unavailable", "This listing is no longer available.");
}
