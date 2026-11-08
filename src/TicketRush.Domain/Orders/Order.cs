using System.Collections.Frozen;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Orders;

/// <summary>
/// Turns a hold into a paid order (spec 08). Every arrow of the state diagram is one method; the legal
/// <c>from</c> states live in one transition table (<see cref="Transitions"/>), so each method stays tiny and
/// the whole machine can be read, and tested, as a table. An illegal transition throws
/// <see cref="DomainException"/> with code <c>order.illegal_transition</c> and changes nothing.
/// <code>
/// Pending ─StartPayment→ AwaitingPayment ─MarkPaid→ Paid ─RequestRefundForCancelledEvent→ RefundPending ─MarkRefunded→ Refunded
///    │                     │  ▲ StartPayment (retry)                                          ▲
///    │          MarkPaymentFailed│                                                            │ MarkUnfulfillable
///    │                     ▼  │                                                              │ (from Expired, AwaitingPayment, PaymentFailed)
///    │                  PaymentFailed ─MarkPaid→ Paid
///    ├─Cancel→ Cancelled
///    └─Expire (also from AwaitingPayment / PaymentFailed)→ Expired ─MarkPaid (late success, seats free)→ Paid
/// </code>
/// <para>
/// Late webhooks (spec 08): the handler checks <see cref="Status"/> first. A <c>Failed</c> on a <see cref="OrderStatus.Paid"/>
/// order, and a repeated <c>Succeeded</c> on one, are ignored by the handler; the domain throws for both because
/// <see cref="MarkPaymentFailed"/> and <see cref="MarkPaid"/> are not legal from <c>Paid</c>.
/// <see cref="IsTerminalForPayment"/> tells the handler when to skip.
/// </para>
/// </summary>
public sealed class Order : AggregateRoot, IAuditable, IRefundable
{
    /// <summary>
    /// Upper bound on one line's quantity. A hold is capped at 8 seats by default (<c>Holds:MaxSeatsPerHold</c>) and
    /// organizers set the per-user phase limit; 100 is a generous hard ceiling that keeps line and order totals far
    /// from <see cref="long"/> overflow while never rejecting a real purchase.
    /// </summary>
    public const int MaxQuantityPerItem = 100;

    private static readonly FrozenDictionary<Trigger, Transition> Transitions = new Dictionary<Trigger, Transition>
    {
        [Trigger.StartPayment] = new(OrderStatus.AwaitingPayment, OrderStatus.Pending, OrderStatus.PaymentFailed),
        [Trigger.MarkPaid] = new(OrderStatus.Paid, OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed, OrderStatus.Expired),
        [Trigger.MarkPaymentFailed] = new(OrderStatus.PaymentFailed, OrderStatus.AwaitingPayment),
        [Trigger.Cancel] = new(OrderStatus.Cancelled, OrderStatus.Pending),
        [Trigger.Expire] = new(OrderStatus.Expired, OrderStatus.Pending, OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed),
        [Trigger.MarkUnfulfillable] = new(OrderStatus.RefundPending, OrderStatus.Expired, OrderStatus.AwaitingPayment, OrderStatus.PaymentFailed),
        [Trigger.RequestRefundForCancelledEvent] = new(OrderStatus.RefundPending, OrderStatus.Paid),
        [Trigger.MarkRefunded] = new(OrderStatus.Refunded, OrderStatus.RefundPending),
    }.ToFrozenDictionary();

    private readonly List<OrderItem> _items = [];

    private Order(
        Guid userId,
        Guid eventId,
        Guid holdId,
        IReadOnlyList<OrderItem> items,
        Money total,
        BuyerInfo buyer,
        DateTimeOffset expiresAt)
        : base(Guid.CreateVersion7())
    {
        UserId = userId;
        EventId = eventId;
        HoldId = holdId;
        _items.AddRange(items);
        Total = total;
        Buyer = new BuyerInfo(buyer.Name.Trim(), buyer.Email.Trim(), buyer.Phone?.Trim());
        ExpiresAt = expiresAt.ToUniversalTime();
        Status = OrderStatus.Pending;
    }

    // Required by EF Core.
    private Order()
    {
        Total = Money.Zero();
        Buyer = new BuyerInfo(string.Empty, string.Empty, null);
    }

    private enum Trigger
    {
        StartPayment,
        MarkPaid,
        MarkPaymentFailed,
        Cancel,
        Expire,
        MarkUnfulfillable,
        RequestRefundForCancelledEvent,
        MarkRefunded,
    }

    public Guid UserId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid HoldId { get; private set; }

    /// <summary>Lines copied from the hold; never recomputed from the live catalog.</summary>
    public IReadOnlyList<OrderItem> Items => _items.AsReadOnly();

    public Money Total { get; private set; }

    public BuyerInfo Buyer { get; private set; }

    public OrderStatus Status { get; private set; }

    /// <summary>The hold's expiry. A payment attempt expires 30 s earlier (<see cref="PaymentAttempt.GatewayMargin"/>).</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>One of <see cref="RefundReasons"/>; set when the order enters <see cref="OrderStatus.RefundPending"/>.</summary>
    public string? RefundReason { get; private set; }

    /// <summary>The refund worker's retry state while the order is <see cref="OrderStatus.RefundPending"/>.</summary>
    public RefundTracking Refund { get; private set; } = RefundTracking.None;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Paid, Refunded or Cancelled: payment webhooks must not change the order any more.</summary>
    public bool IsTerminalForPayment => Status is OrderStatus.Paid or OrderStatus.Refunded or OrderStatus.Cancelled;

    /// <summary>Pending, AwaitingPayment or PaymentFailed: the hold is still reserved for this order (one open order per hold).</summary>
    public bool IsOpen => Status is OrderStatus.Pending or OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed;

    /// <summary>Starts a <see cref="OrderStatus.Pending"/> order. <paramref name="total"/> must equal the sum of the lines.</summary>
    public static Order Create(
        Guid userId,
        Guid eventId,
        Guid holdId,
        IReadOnlyList<OrderItem> items,
        Money total,
        BuyerInfo buyer,
        DateTimeOffset expiresAt,
        DateTimeOffset now)
    {
        if (userId == Guid.Empty || eventId == Guid.Empty || holdId == Guid.Empty)
        {
            throw new DomainException("order.invalid_reference", "An order needs a user, an event and a hold.");
        }

        if (expiresAt <= now)
        {
            throw new DomainException("order.invalid_expiry", "An order must expire in the future.");
        }

        OrderValidator.EnsureBuyer(buyer);
        OrderValidator.EnsureItemsAndTotal(items, total);
        return new Order(userId, eventId, holdId, items, total, buyer, expiresAt);
    }

    /// <summary>Pending or PaymentFailed → AwaitingPayment. Refused (<c>order.expired</c>) once the hold is gone.</summary>
    public void StartPayment(DateTimeOffset now)
    {
        if (now >= ExpiresAt && Transitions[Trigger.StartPayment].Allows(Status))
        {
            throw new DomainException("order.expired", "The hold behind this order has expired.");
        }

        MoveTo(Trigger.StartPayment);
    }

    /// <summary>
    /// AwaitingPayment, PaymentFailed or Expired (late success, seats still free) → Paid. The caller claims the inventory first.
    /// </summary>
    public void MarkPaid(DateTimeOffset now)
    {
        MoveTo(Trigger.MarkPaid);
        PaidAt = now;
        Raise(new OrderPaid(Id, UserId, EventId, HoldId, Total.Amount, Total.Currency, TicketCount(), now, PaidLines()));
    }

    /// <summary>AwaitingPayment → PaymentFailed (the buyer may retry via <see cref="StartPayment"/> while the hold lives).</summary>
    public void MarkPaymentFailed() => MoveTo(Trigger.MarkPaymentFailed);

    /// <summary>Pending → Cancelled. Raises <see cref="OrderCancelled"/> so the hold is released.</summary>
    public void Cancel()
    {
        MoveTo(Trigger.Cancel);
        Raise(new OrderCancelled(Id, UserId, EventId, HoldId));
    }

    /// <summary>Pending, AwaitingPayment or PaymentFailed → Expired (hold ran out). Raises <see cref="OrderExpired"/>.</summary>
    public void Expire()
    {
        MoveTo(Trigger.Expire);
        Raise(new OrderExpired(Id, UserId, EventId, HoldId));
    }

    /// <summary>
    /// Expired, AwaitingPayment or PaymentFailed → RefundPending: the money arrived but the seats are gone (C3).
    /// </summary>
    public void MarkUnfulfillable() => MarkUnfulfillable(RefundReasons.SeatsUnavailable);

    /// <summary>
    /// Same transition with the reason the order cannot be fulfilled (one of <see cref="RefundReasons"/>), e.g.
    /// <see cref="RefundReasons.EventCancelled"/> when the money arrives after the organizer cancelled the event.
    /// </summary>
    public void MarkUnfulfillable(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("order.invalid_refund_reason", "A refund needs a reason.");
        }

        RequestRefund(Trigger.MarkUnfulfillable, reason);
    }

    /// <summary>Paid → RefundPending because the organizer cancelled the event.</summary>
    public void RequestRefundForCancelledEvent() => RequestRefund(Trigger.RequestRefundForCancelledEvent, RefundReasons.EventCancelled);

    /// <summary>RefundPending → Refunded once the gateway confirmed the refund.</summary>
    public void MarkRefunded()
    {
        MoveTo(Trigger.MarkRefunded);
        Raise(new OrderRefunded(Id, UserId, Total.Amount, Total.Currency));
    }

    /// <inheritdoc />
    public void RecordRefundDecline(string error, DateTimeOffset? retryAt) => Track(Refund.Declined(error, retryAt));

    /// <inheritdoc />
    public void ParkRefund(string error) => Track(Refund.Parked(error));

    /// <inheritdoc />
    public void ResumeRefund() => Track(Refund.Resumed());

    private void Track(RefundTracking next)
    {
        EnsureAllowed(Trigger.MarkRefunded);
        Refund = next;
    }

    /// <summary>
    /// One valid ticket per seat and per general-admission unit, for the inventory strategy to insert before
    /// <see cref="MarkPaid"/>. Legal exactly where <see cref="MarkPaid"/> is.
    /// </summary>
    public IReadOnlyList<Ticket> IssueTickets()
    {
        EnsureAllowed(Trigger.MarkPaid);
        return _items
            .SelectMany(item => Enumerable.Range(0, item.Quantity).Select(_ => Ticket.Issue(Id, EventId, item.TierId, item.SeatIndex, UserId)))
            .ToList();
    }

    private List<OrderPaidLine> PaidLines() =>
        _items.Select(item => new OrderPaidLine(item.TierId, item.SeatIndex, item.Quantity)).ToList();

    private int TicketCount() => _items.Sum(item => item.Quantity);

    private void RequestRefund(Trigger trigger, string reason)
    {
        MoveTo(trigger);
        RefundReason = reason;
        Raise(new RefundRequested(Id, UserId, EventId, Total.Amount, Total.Currency, reason));
    }

    private void MoveTo(Trigger trigger)
    {
        EnsureAllowed(trigger);
        Status = Transitions[trigger].To;
    }

    private void EnsureAllowed(Trigger trigger)
    {
        if (!Transitions[trigger].Allows(Status))
        {
            throw new DomainException("order.illegal_transition", $"Cannot {trigger} an order that is {Status}.");
        }
    }

    private sealed class Transition(OrderStatus to, params OrderStatus[] from)
    {
        public OrderStatus To { get; } = to;

        public bool Allows(OrderStatus status) => from.Contains(status);
    }
}
