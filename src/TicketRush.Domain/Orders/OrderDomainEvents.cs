using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>
/// The order is paid and its seats/tickets are claimed. Consumers (all idempotent): <c>inventory_commit.lua</c> (07),
/// leave the queue (06), ticket email (09), dashboard counters (15). Raised for a late success on an expired order too,
/// when the hold is gone, so <see cref="Lines"/> carries the seat indexes and GA quantities the inventory commit needs.
/// </summary>
public sealed record OrderPaid(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    Guid HoldId,
    long TotalAmount,
    string Currency,
    int TicketCount,
    DateTimeOffset PaidAt,
    IReadOnlyList<OrderPaidLine> Lines,
    OrderKind Kind = OrderKind.Primary) : IDomainEvent;

/// <summary>
/// <see cref="Primary"/>: bought from the sale (a Redis hold). <see cref="Resale"/>: a listed ticket bought from its owner
/// (spec 14); the seat is already sold, so the inventory consumers of <see cref="OrderPaid"/> skip it. Messages written before
/// spec 14 have no kind and read as <see cref="Primary"/>.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<OrderKind>))]
public enum OrderKind
{
    Primary,
    Resale,
}

/// <summary>One paid line: a single seat (<see cref="SeatIndex"/> set, quantity 1) or a general-admission quantity.</summary>
public sealed record OrderPaidLine(Guid TierId, int? SeatIndex, int Quantity);

/// <summary>The buyer cancelled a pending order: release the hold.</summary>
public sealed record OrderCancelled(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId) : IDomainEvent;

/// <summary>The hold ran out before payment: release the hold and tell the buyer.</summary>
public sealed record OrderExpired(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId) : IDomainEvent;

/// <summary>
/// Money must go back to the buyer (<see cref="RefundReasons"/>). The refund worker calls the gateway with
/// <c>idempotencyKey = OrderId</c>.
/// </summary>
public sealed record RefundRequested(Guid OrderId, Guid UserId, Guid EventId, long Amount, string Currency, string Reason) : IDomainEvent;

/// <summary>
/// Money reached one payment session that cannot pay for its order (for example a second session paid an order that
/// is already paid): refund exactly that session. Unlike <see cref="RefundRequested"/> the order does not change. The
/// refund worker calls the gateway with <c>idempotencyKey = $"attempt:{AttemptId}"</c>, so it never collides with the
/// order-level refund keyed by the order id.
/// </summary>
public sealed record PaymentRefundRequested(
    Guid AttemptId,
    Guid OrderId,
    string Provider,
    string SessionId,
    long Amount,
    string Currency,
    string Reason) : IDomainEvent;

/// <summary>The gateway confirmed the refund: notify the buyer.</summary>
public sealed record OrderRefunded(Guid OrderId, Guid UserId, long Amount, string Currency) : IDomainEvent;

/// <summary>The gateway confirmed the refund of one payment session (<see cref="PaymentRefundRequested"/>): notify the buyer.</summary>
public sealed record PaymentRefunded(Guid AttemptId, Guid OrderId, long Amount, string Currency) : IDomainEvent;
