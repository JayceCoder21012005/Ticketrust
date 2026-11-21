using TicketRush.Domain.Primitives;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Domain.Tests.Waitlist;

public sealed class WaitlistTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 3, 0, 0, TimeSpan.Zero);
    private static readonly Guid Event = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static WaitlistEntry Join(int quantity = 2, Guid? tier = null) => WaitlistEntry.Join(Event, User, tier, quantity, Now);

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Quantity_is_one_to_four(int quantity)
    {
        Should.Throw<DomainException>(() => Join(quantity)).Code.ShouldBe("waitlist.invalid_quantity");
    }

    [Fact]
    public void A_new_entry_waits_and_is_live()
    {
        var entry = Join();

        entry.Status.ShouldBe(WaitlistStatus.Waiting);
        entry.IsLive.ShouldBeTrue();
        entry.QueuedAt.ShouldBe(Now);
        entry.Misses.ShouldBe(0);
    }

    [Fact]
    public void An_offer_then_a_paid_order_converts_it()
    {
        var entry = Join();
        var hold = Guid.NewGuid();

        entry.Offer(hold, Now.AddMinutes(15));
        entry.Status.ShouldBe(WaitlistStatus.Offered);
        entry.OfferHoldId.ShouldBe(hold);
        entry.Convert();

        entry.Status.ShouldBe(WaitlistStatus.Converted);
        entry.IsLive.ShouldBeFalse();
    }

    [Fact]
    public void The_first_missed_offer_sends_it_to_the_back_once_and_the_second_expires_it()
    {
        var entry = Join();

        entry.Offer(Guid.NewGuid(), Now.AddMinutes(15));
        entry.Miss(Now.AddMinutes(16));
        entry.Status.ShouldBe(WaitlistStatus.Waiting);
        entry.QueuedAt.ShouldBe(Now.AddMinutes(16));
        entry.OfferHoldId.ShouldBeNull();

        entry.Offer(Guid.NewGuid(), Now.AddMinutes(45));
        entry.Miss(Now.AddMinutes(46));
        entry.Status.ShouldBe(WaitlistStatus.Expired);
        entry.IsLive.ShouldBeFalse();
    }

    [Fact]
    public void Leaving_ends_it_from_waiting_or_offered()
    {
        var waiting = Join();
        waiting.Leave();
        waiting.Status.ShouldBe(WaitlistStatus.Left);

        var offered = Join();
        offered.Offer(Guid.NewGuid(), Now.AddMinutes(15));
        offered.Leave();
        offered.Status.ShouldBe(WaitlistStatus.Left);
    }

    [Fact]
    public void Only_a_waiting_entry_gets_an_offer()
    {
        var entry = Join();
        entry.Offer(Guid.NewGuid(), Now.AddMinutes(15));

        Should.Throw<DomainException>(() => entry.Offer(Guid.NewGuid(), Now.AddMinutes(15))).Code.ShouldBe("waitlist.illegal_transition");
    }

    [Fact]
    public void An_offer_raises_the_event_the_notifications_send()
    {
        var entry = Join();
        var hold = Guid.NewGuid();

        entry.Offer(hold, Now.AddMinutes(15));

        entry.DomainEvents.OfType<WaitlistOfferIssued>().ShouldHaveSingleItem()
            .ShouldBe(new WaitlistOfferIssued(entry.Id, Event, User, hold, Now.AddMinutes(15)));
    }

    [Fact]
    public void Matching_takes_any_tier_or_the_same_and_never_more_units_than_returned()
    {
        var tier = Guid.NewGuid();
        Join(2, null).Wants(tier, 2).ShouldBeTrue();
        Join(2, tier).Wants(tier, 3).ShouldBeTrue();
        Join(2, tier).Wants(Guid.NewGuid(), 2).ShouldBeFalse();
        Join(3, null).Wants(tier, 2).ShouldBeFalse();
    }

    [Fact]
    public void A_cancelled_event_closes_a_waiting_or_offered_entry()
    {
        var waiting = Join();
        waiting.Close();
        waiting.Status.ShouldBe(WaitlistStatus.Closed);
        waiting.IsLive.ShouldBeFalse();

        var offered = Join();
        offered.Offer(Guid.NewGuid(), Now.AddMinutes(15));
        offered.Close();
        offered.Status.ShouldBe(WaitlistStatus.Closed);
        offered.OfferHoldId.ShouldBeNull();
    }

    [Fact]
    public void A_finished_entry_cannot_be_closed()
    {
        var left = Join();
        left.Leave();

        Should.Throw<DomainException>(left.Close).Code.ShouldBe("waitlist.illegal_transition");
    }

    [Fact]
    public void A_listing_offer_records_the_listing_and_a_miss_clears_it()
    {
        var entry = Join(quantity: 1);
        var listing = Guid.NewGuid();

        entry.OfferListing(listing, Now.AddMinutes(10));

        entry.Status.ShouldBe(WaitlistStatus.Offered);
        entry.OfferListingId.ShouldBe(listing);
        entry.OfferHoldId.ShouldBeNull();
        entry.DomainEvents.OfType<WaitlistOfferIssued>().Single().ListingId.ShouldBe(listing);
        entry.Miss(Now.AddMinutes(10));
        entry.OfferListingId.ShouldBeNull();
    }

    [Fact]
    public void A_withdrawn_listing_offer_puts_the_entry_back_in_its_place_without_a_miss()
    {
        var entry = Join(quantity: 1);
        entry.OfferListing(Guid.NewGuid(), Now.AddMinutes(10));

        entry.WithdrawOffer();

        entry.Status.ShouldBe(WaitlistStatus.Waiting);
        entry.Misses.ShouldBe(0);
        entry.QueuedAt.ShouldBe(Now);
        entry.OfferListingId.ShouldBeNull();
    }
}
