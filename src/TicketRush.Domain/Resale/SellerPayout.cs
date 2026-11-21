using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Resale;

public enum SellerPayoutStatus
{
    Pending,
    Paid,
    Cancelled,
}

/// <summary>
/// What a sold listing owes its seller (spec 14). A mock job pays it after the event ends; a cancelled event cancels it, since the
/// seller's original order is refunded instead. Real bank payouts are out of scope. One per listing.
/// </summary>
public sealed class SellerPayout : AggregateRoot, IAuditable
{
    private SellerPayout(ResaleListing listing)
        : base(Guid.CreateVersion7())
    {
        ListingId = listing.Id;
        SellerId = listing.SellerId;
        EventId = listing.EventId;
        Amount = listing.Price;
        Status = SellerPayoutStatus.Pending;
    }

    // Required by EF Core.
    private SellerPayout()
    {
        Amount = Money.Zero();
    }

    public Guid ListingId { get; private set; }

    public Guid SellerId { get; private set; }

    public Guid EventId { get; private set; }

    public Money Amount { get; private set; }

    public SellerPayoutStatus Status { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static SellerPayout Create(ResaleListing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        if (listing.Status != ResaleListingStatus.Sold)
        {
            throw new DomainException("payout.not_sold", "Only a sold listing is paid out.");
        }

        return new SellerPayout(listing);
    }

    public void MarkPaid(DateTimeOffset now)
    {
        RequirePending();
        Status = SellerPayoutStatus.Paid;
        PaidAt = now;
    }

    public void Cancel()
    {
        RequirePending();
        Status = SellerPayoutStatus.Cancelled;
    }

    private void RequirePending()
    {
        if (Status != SellerPayoutStatus.Pending)
        {
            throw new DomainException("payout.illegal_transition", $"A payout that is {Status} cannot change.");
        }
    }
}
