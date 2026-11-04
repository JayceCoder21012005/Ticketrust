using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>
/// The order is paid and its seats/tickets are claimed. Consumers (all idempotent): <c>inventory_commit.lua</c> (07),
/// leave the queue (06), ticket email (09), dashboard counters (15). Raised for a late success on an expired order too.
/// </summary>
public sealed record OrderPaid(
    Guid OrderId,
    Guid UserId,
    Guid EventId,
    Guid HoldId,
    long TotalAmount,
    string Currency,
    int TicketCount,
    DateTimeOffset PaidAt) : IDomainEvent;

/// <summary>The buyer cancelled a pending order: release the hold.</summary>
public sealed record OrderCancelled(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId) : IDomainEvent;

/// <summary>The hold ran out before payment: release the hold and tell the buyer.</summary>
public sealed record OrderExpired(Guid OrderId, Guid UserId, Guid EventId, Guid HoldId) : IDomainEvent;

/// <summary>
/// Money must go back to the buyer (<see cref="RefundReasons"/>). The refund worker calls the gateway with
/// <c>idempotencyKey = OrderId</c>.
/// </summary>
public sealed record RefundRequested(Guid OrderId, Guid UserId, Guid EventId, long Amount, string Currency, string Reason) : IDomainEvent;

/// <summary>The gateway confirmed the refund: notify the buyer.</summary>
public sealed record OrderRefunded(Guid OrderId, Guid UserId, long Amount, string Currency) : IDomainEvent;
