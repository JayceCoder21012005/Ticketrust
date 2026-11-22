using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Tests.Resale;

public sealed class ResaleTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Money Face = new(1_000_000);
    private static readonly Guid Buyer = Guid.CreateVersion7();

    private static Ticket NewTicket(int? seat = 5) =>
        Ticket.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), seat, Guid.CreateVersion7());

    private static ResaleListing NewListing(Ticket? ticket = null, long price = 900_000) =>
        ResaleListing.Create(ticket ?? NewTicket(), new Money(price), Face, Now);

    [Fact]
    public void Listing_a_valid_ticket_lists_it_and_opens_a_ten_minute_waitlist_window()
    {
        var ticket = NewTicket();

        var listing = NewListing(ticket);

        ticket.Status.ShouldBe(TicketStatus.Listed);
        listing.Status.ShouldBe(ResaleListingStatus.Active);
        listing.SellerId.ShouldBe(ticket.OwnerUserId);
        listing.SeatIndex.ShouldBe(5);
        listing.WaitlistUntil.ShouldBe(Now.AddMinutes(10));
        listing.DomainEvents.OfType<ListingCreated>().Single().ListingId.ShouldBe(listing.Id);
    }

    [Fact]
    public void A_price_above_face_value_is_refused()
    {
        Should.Throw<DomainException>(() => NewListing(price: 1_000_001)).Code.ShouldBe("resale.price_above_cap");
        NewListing(price: 1_000_000).Price.Amount.ShouldBe(1_000_000);
    }

    [Fact]
    public void A_price_below_one_dong_is_refused() =>
        Should.Throw<DomainException>(() => NewListing(price: 0)).Code.ShouldBe("resale.invalid_price");

    [Theory]
    [InlineData(TicketStatus.CheckedIn)]
    [InlineData(TicketStatus.Listed)]
    [InlineData(TicketStatus.Voided)]
    public void Only_a_valid_ticket_can_be_listed(TicketStatus status)
    {
        var ticket = TicketIn(status);

        Should.Throw<DomainException>(() => NewListing(ticket)).Code.ShouldBe("resale.not_allowed");
    }

    [Fact]
    public void The_sale_window_closes_two_hours_before_the_start()
    {
        var startsAt = Now.AddHours(3);

        ResaleRules.IsOpen(startsAt, Now).ShouldBeTrue();
        ResaleRules.IsOpen(startsAt, Now.AddHours(1)).ShouldBeFalse();
    }

    [Fact]
    public void During_the_window_only_the_offered_waiter_may_reserve_and_after_it_anyone_but_the_seller()
    {
        var listing = NewListing();
        var waiter = Guid.CreateVersion7();
        listing.OfferTo(waiter);

        listing.IsOpenTo(Buyer, Now).ShouldBeFalse();
        listing.IsOpenTo(waiter, Now).ShouldBeTrue();
        listing.IsOpenTo(Buyer, Now.AddMinutes(10)).ShouldBeTrue();
        listing.IsOpenTo(listing.SellerId, Now.AddMinutes(10)).ShouldBeFalse();
    }

    [Fact]
    public void Opening_to_the_public_ends_the_window_at_once()
    {
        var listing = NewListing();

        listing.OpenToPublic();

        listing.WaitlistUntil.ShouldBeNull();
        listing.IsOpenTo(Buyer, Now).ShouldBeTrue();
    }

    [Fact]
    public void Reserving_then_releasing_puts_the_listing_back_on_sale()
    {
        var listing = NewListing();
        listing.OpenToPublic();
        var order = Guid.CreateVersion7();

        listing.Reserve(order, Buyer, Now);
        listing.Status.ShouldBe(ResaleListingStatus.Reserved);
        listing.ReservedOrderId.ShouldBe(order);

        listing.ReleaseReservation(Guid.CreateVersion7()).ShouldBeFalse();
        listing.ReleaseReservation(order).ShouldBeTrue();
        listing.Status.ShouldBe(ResaleListingStatus.Active);
        listing.ReservedOrderId.ShouldBeNull();
    }

    [Fact]
    public void The_seller_cannot_reserve_their_own_listing()
    {
        var listing = NewListing();
        listing.OpenToPublic();

        Should.Throw<DomainException>(() => listing.Reserve(Guid.CreateVersion7(), listing.SellerId, Now)).Code.ShouldBe("resale.own_listing");
    }

    [Fact]
    public void A_reserved_or_hidden_listing_is_unavailable()
    {
        var listing = NewListing();
        Should.Throw<DomainException>(() => listing.Reserve(Guid.CreateVersion7(), Buyer, Now)).Code.ShouldBe("resale.listing_unavailable");

        listing.OpenToPublic();
        listing.Reserve(Guid.CreateVersion7(), Buyer, Now);
        Should.Throw<DomainException>(() => listing.Reserve(Guid.CreateVersion7(), Guid.CreateVersion7(), Now)).Code.ShouldBe("resale.listing_unavailable");
    }

    [Fact]
    public void Selling_needs_the_reservation_of_that_order_or_a_listing_still_active()
    {
        var order = Guid.CreateVersion7();
        var reserved = NewListing();
        reserved.OpenToPublic();
        reserved.Reserve(order, Buyer, Now);
        Should.Throw<DomainException>(() => reserved.MarkSold(Guid.CreateVersion7())).Code.ShouldBe("resale.listing_unavailable");
        reserved.MarkSold(order);
        reserved.Status.ShouldBe(ResaleListingStatus.Sold);

        // A late payment whose reservation ran out still sells a listing nobody else took.
        var active = NewListing();
        active.MarkSold(order);
        active.ReservedOrderId.ShouldBe(order);
    }

    [Fact]
    public void Cancelling_an_active_listing_gives_the_ticket_back()
    {
        var ticket = NewTicket();
        var listing = NewListing(ticket);

        listing.Cancel(ticket);

        listing.Status.ShouldBe(ResaleListingStatus.Cancelled);
        ticket.Status.ShouldBe(TicketStatus.Valid);
    }

    [Fact]
    public void A_reserved_listing_cannot_be_cancelled_by_the_seller()
    {
        var ticket = NewTicket();
        var listing = NewListing(ticket);
        listing.OpenToPublic();
        listing.Reserve(Guid.CreateVersion7(), Buyer, Now);

        Should.Throw<DomainException>(() => listing.Cancel(ticket)).Code.ShouldBe("resale.listing_unavailable");
    }

    [Fact]
    public void A_payout_is_the_listing_price_and_is_paid_or_cancelled_once()
    {
        var listing = NewListing(price: 800_000);
        listing.MarkSold(Guid.CreateVersion7());

        var payout = SellerPayout.Create(listing);
        payout.Amount.Amount.ShouldBe(800_000);
        payout.SellerId.ShouldBe(listing.SellerId);
        payout.MarkPaid(Now);
        payout.Status.ShouldBe(SellerPayoutStatus.Paid);
        Should.Throw<DomainException>(payout.Cancel);
    }

    [Fact]
    public void A_transfer_lists_the_ticket_for_48_hours()
    {
        var ticket = NewTicket();

        var transfer = TicketTransfer.Create(ticket, "seller@example.com", "  Friend@Example.com ", Now);

        ticket.Status.ShouldBe(TicketStatus.Listed);
        transfer.ToEmail.ShouldBe("friend@example.com");
        transfer.ExpiresAt.ShouldBe(Now.AddHours(48));
        transfer.Status.ShouldBe(TicketTransferStatus.Pending);
        transfer.DomainEvents.OfType<TransferCreated>().Single().TransferId.ShouldBe(transfer.Id);
    }

    [Fact]
    public void A_transfer_to_your_own_email_is_refused() =>
        Should.Throw<DomainException>(() => TicketTransfer.Create(NewTicket(), "Me@Example.com", "me@example.com", Now)).Code.ShouldBe("transfer.self");

    [Fact]
    public void Only_a_valid_ticket_can_be_transferred() =>
        Should.Throw<DomainException>(() => TicketTransfer.Create(TicketIn(TicketStatus.CheckedIn), "a@example.com", "b@example.com", Now))
            .Code.ShouldBe("transfer.not_allowed");

    [Fact]
    public void A_transfer_is_accepted_once_and_not_after_it_expired()
    {
        var transfer = TicketTransfer.Create(NewTicket(), "a@example.com", "b@example.com", Now);
        var late = TicketTransfer.Create(NewTicket(), "a@example.com", "b@example.com", Now);
        var recipient = Guid.CreateVersion7();

        transfer.Accept(recipient, Now.AddHours(1));
        transfer.Status.ShouldBe(TicketTransferStatus.Accepted);
        transfer.ToUserId.ShouldBe(recipient);
        Should.Throw<DomainException>(() => transfer.Accept(recipient, Now.AddHours(1))).Code.ShouldBe("transfer.not_allowed");
        Should.Throw<DomainException>(() => late.Accept(recipient, Now.AddHours(48))).Code.ShouldBe("transfer.not_allowed");
    }

    [Fact]
    public void Cancelling_or_expiring_a_transfer_gives_the_ticket_back()
    {
        var first = NewTicket();
        var second = NewTicket();
        var cancelled = TicketTransfer.Create(first, "a@example.com", "b@example.com", Now);
        var expired = TicketTransfer.Create(second, "a@example.com", "b@example.com", Now);

        cancelled.Cancel(first);
        expired.Expire(second);

        cancelled.Status.ShouldBe(TicketTransferStatus.Cancelled);
        expired.Status.ShouldBe(TicketTransferStatus.Expired);
        first.Status.ShouldBe(TicketStatus.Valid);
        second.Status.ShouldBe(TicketStatus.Valid);
    }

    [Fact]
    public void Handing_over_voids_without_returning_the_ticket_and_the_reissue_has_a_new_owner_and_secret()
    {
        var ticket = NewTicket();
        ticket.List();
        var newOwner = Guid.CreateVersion7();
        var order = Guid.CreateVersion7();

        Should.Throw<DomainException>(() => ticket.Reissue(order, newOwner));
        ticket.HandOver();
        var reissued = ticket.Reissue(order, newOwner);

        ticket.Status.ShouldBe(TicketStatus.Voided);
        ticket.DomainEvents.OfType<TicketReturned>().ShouldBeEmpty();
        reissued.Status.ShouldBe(TicketStatus.Valid);
        reissued.OwnerUserId.ShouldBe(newOwner);
        reissued.OrderId.ShouldBe(order);
        (reissued.EventId, reissued.TierId, reissued.SeatIndex).ShouldBe((ticket.EventId, ticket.TierId, ticket.SeatIndex));
        reissued.Id.ShouldNotBe(ticket.Id);
        reissued.QrSecret.ShouldNotBe(ticket.QrSecret);
    }

    [Fact]
    public void A_resale_order_holds_the_listing_and_its_paid_event_says_resale()
    {
        var listing = Guid.CreateVersion7();
        var order = Order.CreateResale(
            Buyer, Guid.CreateVersion7(), listing, new OrderItem(Guid.CreateVersion7(), 5, new Money(900_000), 1),
            new BuyerInfo("An", "an@example.com", null), Now.AddMinutes(10), Now);

        order.Kind.ShouldBe(OrderKind.Resale);
        order.ListingId.ShouldBe(listing);
        order.HoldId.ShouldBe(listing);
        Should.Throw<DomainException>(() => order.IssueTickets());

        order.StartPayment(Now);
        order.MarkPaid(Now);
        order.DomainEvents.OfType<OrderPaid>().Single().Kind.ShouldBe(OrderKind.Resale);
    }

    private static Ticket TicketIn(TicketStatus status)
    {
        var ticket = NewTicket();
        switch (status)
        {
            case TicketStatus.CheckedIn:
                ticket.CheckIn(Guid.CreateVersion7(), Now);
                break;
            case TicketStatus.Listed:
                ticket.List();
                break;
            case TicketStatus.Voided:
                ticket.Void();
                break;
        }

        return ticket;
    }
}
