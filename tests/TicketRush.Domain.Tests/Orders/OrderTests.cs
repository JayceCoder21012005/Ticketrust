using System.Text.Json;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using static TicketRush.Domain.Tests.Orders.OrderTestData;

namespace TicketRush.Domain.Tests.Orders;

public sealed class OrderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Order Create(
        IReadOnlyList<OrderItem>? items = null,
        Money? total = null,
        BuyerInfo? buyer = null,
        DateTimeOffset? expiresAt = null,
        Guid? userId = null) =>
        Order.Create(userId ?? UserId, EventId, HoldId, items ?? SeatedItems(), total ?? new Money(1_600_000), buyer ?? Buyer, expiresAt ?? ExpiresAt, Now);

    [Fact]
    public void Create_copies_the_hold_into_a_pending_order_without_raising_events()
    {
        var order = Create();

        order.Status.ShouldBe(OrderStatus.Pending);
        order.UserId.ShouldBe(UserId);
        order.EventId.ShouldBe(EventId);
        order.HoldId.ShouldBe(HoldId);
        order.Total.ShouldBe(new Money(1_600_000));
        order.Buyer.ShouldBe(Buyer);
        order.ExpiresAt.ShouldBe(ExpiresAt);
        order.PaidAt.ShouldBeNull();
        order.RefundReason.ShouldBeNull();
        order.Items.Count.ShouldBe(2);
        order.Id.ShouldNotBe(Guid.Empty);
        order.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Create_accepts_general_admission_items_with_quantity()
    {
        var order = Create([new OrderItem(TierId, null, new Money(500_000), 3)], new Money(1_500_000));

        order.Items[0].SeatIndex.ShouldBeNull();
        order.Items[0].Quantity.ShouldBe(3);
    }

    [Fact]
    public void Create_rejects_empty_items()
    {
        Should.Throw<DomainException>(() => Create([], new Money(0))).Code.ShouldBe("order.no_items");
    }

    [Fact]
    public void Create_rejects_a_total_that_is_not_the_sum_of_the_lines()
    {
        Should.Throw<DomainException>(() => Create(total: new Money(1_599_999))).Code.ShouldBe("order.total_mismatch");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_rejects_quantity_below_one(int quantity)
    {
        var items = new[] { new OrderItem(TierId, null, new Money(500_000), quantity) };

        Should.Throw<DomainException>(() => Create(items, new Money(0))).Code.ShouldBe("order.invalid_item");
    }

    [Fact]
    public void Create_rejects_a_seated_line_with_quantity_above_one()
    {
        var items = new[] { new OrderItem(TierId, 5, new Money(500_000), 2) };

        Should.Throw<DomainException>(() => Create(items, new Money(1_000_000))).Code.ShouldBe("order.invalid_item");
    }

    [Fact]
    public void Create_rejects_a_negative_seat_index_and_an_empty_tier()
    {
        Should.Throw<DomainException>(() => Create([new OrderItem(TierId, -1, new Money(1), 1)], new Money(1)))
            .Code.ShouldBe("order.invalid_item");
        Should.Throw<DomainException>(() => Create([new OrderItem(Guid.Empty, null, new Money(1), 1)], new Money(1)))
            .Code.ShouldBe("order.invalid_item");
    }

    [Fact]
    public void Create_rejects_mixed_currencies()
    {
        var items = new[]
        {
            new OrderItem(TierId, 1, new Money(10), 1),
            new OrderItem(TierId, 2, new Money(10, "USD"), 1),
        };

        Should.Throw<DomainException>(() => Create(items, new Money(20))).Code.ShouldBe("money.currency_mismatch");
    }

    [Fact]
    public void Create_rejects_a_total_in_another_currency()
    {
        Should.Throw<DomainException>(() => Create(total: new Money(1_600_000, "USD"))).Code.ShouldBe("money.currency_mismatch");
    }

    [Theory]
    [InlineData("", "a@example.com")]
    [InlineData("  ", "a@example.com")]
    [InlineData("A", "")]
    [InlineData("A", "  ")]
    public void Create_requires_buyer_name_and_email(string name, string email)
    {
        Should.Throw<DomainException>(() => Create(buyer: new BuyerInfo(name, email, null)))
            .Code.ShouldBe("order.invalid_buyer");
    }

    [Fact]
    public void Create_allows_a_missing_buyer_phone()
    {
        Create(buyer: new BuyerInfo("A", "a@example.com", null)).Buyer.Phone.ShouldBeNull();
    }

    [Fact]
    public void Create_requires_an_expiry_in_the_future()
    {
        Should.Throw<DomainException>(() => Create(expiresAt: Now)).Code.ShouldBe("order.invalid_expiry");
        Should.Throw<DomainException>(() => Create(expiresAt: Now.AddSeconds(-1))).Code.ShouldBe("order.invalid_expiry");
    }

    [Fact]
    public void Create_rejects_empty_ids()
    {
        Should.Throw<DomainException>(() => Create(userId: Guid.Empty)).Code.ShouldBe("order.invalid_reference");
    }

    [Fact]
    public void StartPayment_refuses_an_order_whose_hold_has_already_died()
    {
        var order = Pending();

        Should.Throw<DomainException>(() => order.StartPayment(ExpiresAt)).Code.ShouldBe("order.expired");
        order.Status.ShouldBe(OrderStatus.Pending);
    }

    [Fact]
    public void Payment_can_be_retried_after_a_failure_while_the_order_is_valid()
    {
        var order = InStatus(OrderStatus.PaymentFailed);

        order.StartPayment(Now.AddMinutes(2));

        order.Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public void MarkPaid_stamps_PaidAt_and_raises_OrderPaid_with_what_consumers_need()
    {
        var order = InStatus(OrderStatus.AwaitingPayment);
        var paidAt = Now.AddMinutes(3);

        order.MarkPaid(paidAt);

        order.PaidAt.ShouldBe(paidAt);
        var paid = order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBeOfType<OrderPaid>();
        paid.OrderId.ShouldBe(order.Id);
        paid.UserId.ShouldBe(UserId);
        paid.EventId.ShouldBe(EventId);
        paid.HoldId.ShouldBe(HoldId);
        paid.TotalAmount.ShouldBe(1_600_000);
        paid.Currency.ShouldBe("VND");
        paid.TicketCount.ShouldBe(2);
        paid.PaidAt.ShouldBe(paidAt);
        paid.Lines.ShouldBe([new OrderPaidLine(TierId, 12, 1), new OrderPaidLine(TierId, 13, 1)]);
    }

    [Fact]
    public void A_late_success_on_an_expired_order_whose_seats_are_free_pays_it()
    {
        var order = InStatus(OrderStatus.Expired);

        order.MarkPaid(ExpiresAt.AddMinutes(1));

        order.Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    public void MarkPaid_on_a_paid_order_throws_so_handlers_must_check_the_state_first()
    {
        var order = InStatus(OrderStatus.Paid);

        Should.Throw<DomainException>(() => order.MarkPaid(Now)).Code.ShouldBe("order.illegal_transition");
    }

    [Fact]
    public void A_late_failure_on_a_paid_order_throws_and_leaves_it_paid()
    {
        var order = InStatus(OrderStatus.Paid);

        Should.Throw<DomainException>(() => order.MarkPaymentFailed());

        order.Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    public void Cancel_raises_OrderCancelled_so_the_hold_can_be_released()
    {
        var order = Pending();

        order.Cancel();

        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBe(new OrderCancelled(order.Id, UserId, EventId, HoldId));
    }

    [Fact]
    public void Expire_raises_OrderExpired_carrying_the_hold()
    {
        var order = InStatus(OrderStatus.AwaitingPayment);

        order.Expire();

        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBe(new OrderExpired(order.Id, UserId, EventId, HoldId));
    }

    [Fact]
    public void MarkUnfulfillable_requests_a_refund_for_the_full_total()
    {
        var order = InStatus(OrderStatus.Expired);

        order.MarkUnfulfillable();

        order.RefundReason.ShouldBe(RefundReasons.SeatsUnavailable);
        order.DequeueDomainEvents().ShouldHaveSingleItem()
            .ShouldBe(new RefundRequested(order.Id, UserId, EventId, 1_600_000, "VND", RefundReasons.SeatsUnavailable));
    }

    [Fact]
    public void RequestRefundForCancelledEvent_moves_a_paid_order_to_refund_pending()
    {
        var order = InStatus(OrderStatus.Paid);

        order.RequestRefundForCancelledEvent();

        order.Status.ShouldBe(OrderStatus.RefundPending);
        order.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBeOfType<RefundRequested>()
            .Reason.ShouldBe(RefundReasons.EventCancelled);
    }

    [Fact]
    public void MarkRefunded_raises_OrderRefunded_for_the_notification()
    {
        var order = InStatus(OrderStatus.RefundPending);

        order.MarkRefunded();

        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBe(new OrderRefunded(order.Id, UserId, 1_600_000, "VND"));
    }

    [Theory]
    [InlineData(OrderStatus.Pending, false, true)]
    [InlineData(OrderStatus.AwaitingPayment, false, true)]
    [InlineData(OrderStatus.PaymentFailed, false, true)]
    [InlineData(OrderStatus.Expired, false, false)]
    [InlineData(OrderStatus.Paid, true, false)]
    [InlineData(OrderStatus.Cancelled, true, false)]
    [InlineData(OrderStatus.Refunded, true, false)]
    [InlineData(OrderStatus.RefundPending, false, false)]
    public void Classifies_terminal_and_open_states(OrderStatus status, bool terminalForPayment, bool open)
    {
        var order = InStatus(status);

        order.IsTerminalForPayment.ShouldBe(terminalForPayment);
        order.IsOpen.ShouldBe(open);
    }

    [Fact]
    public void IssueTickets_creates_one_valid_ticket_per_seat_and_per_general_admission_unit()
    {
        var items = new[]
        {
            new OrderItem(TierId, 7, new Money(800_000), 1),
            new OrderItem(Guid.CreateVersion7(), null, new Money(500_000), 2),
        };
        var order = Create(items, new Money(1_800_000));
        order.StartPayment(Now);

        var tickets = order.IssueTickets();

        tickets.Count.ShouldBe(3);
        tickets.ShouldAllBe(t => t.OrderId == order.Id && t.EventId == EventId && t.OwnerUserId == UserId);
        tickets.Count(t => t.SeatIndex == 7).ShouldBe(1);
        tickets.Count(t => t.SeatIndex is null).ShouldBe(2);
        tickets.Select(t => t.Id).Distinct().Count().ShouldBe(3);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.RefundPending)]
    [InlineData(OrderStatus.Refunded)]
    public void IssueTickets_is_only_legal_where_MarkPaid_is(OrderStatus status)
    {
        Should.Throw<DomainException>(() => InStatus(status).IssueTickets()).Code.ShouldBe("order.illegal_transition");
    }

    [Fact]
    public void Every_order_event_round_trips_through_the_outbox_json_options()
    {
        IDomainEvent[] events =
        [
            new OrderPaid(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, "VND", 3, Now, [new OrderPaidLine(Guid.NewGuid(), 7, 1), new OrderPaidLine(Guid.NewGuid(), null, 2)]),
            new OrderCancelled(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new OrderExpired(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            new RefundRequested(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10, "VND", RefundReasons.EventCancelled),
            new OrderRefunded(Guid.NewGuid(), Guid.NewGuid(), 10, "VND"),
        ];

        foreach (var domainEvent in events)
        {
            var json = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json);
            var back = JsonSerializer.Deserialize(json, domainEvent.GetType(), Json).ShouldNotBeNull();
            back.ShouldBeOfType(domainEvent.GetType());
            JsonSerializer.Serialize(back, domainEvent.GetType(), Json).ShouldBe(json);
        }
    }

    [Fact]
    public void OrderPaid_lines_carry_general_admission_quantities_for_the_late_payment_path()
    {
        var gaTier = Guid.CreateVersion7();
        var order = Create([new OrderItem(gaTier, null, new Money(500_000), 3)], new Money(1_500_000));
        order.StartPayment(Now);
        order.Expire();
        order.DequeueDomainEvents();

        order.MarkPaid(ExpiresAt.AddMinutes(1));

        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBeOfType<OrderPaid>()
            .Lines.ShouldBe([new OrderPaidLine(gaTier, null, 3)]);
    }

    [Fact]
    public void Create_rejects_a_quantity_above_the_per_item_maximum()
    {
        var items = new[] { new OrderItem(TierId, null, new Money(1), Order.MaxQuantityPerItem + 1) };

        Should.Throw<DomainException>(() => Create(items, new Money(Order.MaxQuantityPerItem + 1)))
            .Code.ShouldBe("order.invalid_item");
        Should.Throw<DomainException>(() => Create([new OrderItem(TierId, null, new Money(1), int.MaxValue)], new Money(1)))
            .Code.ShouldBe("order.invalid_item");
    }

    [Fact]
    public void Create_accepts_exactly_the_per_item_maximum()
    {
        var quantity = Order.MaxQuantityPerItem;

        var order = Create([new OrderItem(TierId, null, new Money(1_000), quantity)], new Money(1_000L * quantity));

        order.Items[0].Quantity.ShouldBe(quantity);
    }

    [Fact]
    public void Create_turns_an_overflowing_total_into_a_domain_error_instead_of_an_OverflowException()
    {
        var items = new[]
        {
            new OrderItem(TierId, null, new Money(long.MaxValue), 2),
            new OrderItem(TierId, 1, new Money(long.MaxValue), 1),
        };

        Should.Throw<DomainException>(() => Create(items, new Money(1))).Code.ShouldBe("order.total_mismatch");
        Should.Throw<DomainException>(() => Create([new OrderItem(TierId, 1, new Money(long.MaxValue), 1), new OrderItem(TierId, 2, new Money(1), 1)], new Money(1)))
            .Code.ShouldBe("order.total_mismatch");
    }
}
