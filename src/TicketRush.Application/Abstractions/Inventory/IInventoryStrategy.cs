using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Abstractions.Inventory;

/// <summary>
/// Claims the inventory behind a paid order's tickets in the database, which is the final truth for seats (spec 08,
/// C1). Every implementation runs on the caller's current transaction: when any strategy reports a conflict, the
/// caller rolls the whole transaction back, so no claim of any strategy survives. An order may mix seated and
/// general-admission lines; each ticket is handled by exactly one strategy.
/// </summary>
public interface IInventoryStrategy
{
    /// <summary>
    /// Strategies run in ascending order (general admission before seats), so concurrent fulfilments take their locks
    /// in the same order.
    /// </summary>
    int Order { get; }

    /// <summary>Whether this strategy claims <paramref name="ticket"/> (seated: a seat index; general admission: none).</summary>
    bool Handles(Ticket ticket);

    /// <summary>
    /// Claims the inventory for <paramref name="tickets"/> and adds them to the unit of work (written by now or by the
    /// caller's next save, inside the same transaction).
    /// </summary>
    /// <exception cref="InventoryConflictException">The seats or the capacity are no longer available, or a concurrent fulfilment collided.</exception>
    Task ClaimAsync(IReadOnlyList<Ticket> tickets, CancellationToken ct);
}

/// <summary>Database-side purchase limit of one buyer for one event (spec 08 step 4, spec 12).</summary>
public interface ITicketPurchaseLimit
{
    /// <summary>
    /// True when the buyer's non-voided tickets for the event plus <paramref name="adding"/> stay within the event's
    /// per-user limit. Serialises concurrent checks of the same buyer and event until the caller's transaction ends.
    /// </summary>
    Task<bool> AllowsAsync(Guid eventId, Guid userId, int adding, CancellationToken ct);
}

/// <summary>Whether an event still sells tickets, decided under a lock so a concurrent cancellation is ordered before or after us.</summary>
public interface IEventSaleLock
{
    /// <summary>
    /// The event's status (null if it does not exist), read AFTER taking the event's shared sale lock (a transaction-scoped
    /// advisory lock, never a row lock) which is held until the caller's transaction ends. Ending the sale (cancel, end)
    /// takes the exclusive variant in its own transaction, so a cancellation waits for in-flight fulfilments (its refund
    /// sweep then sees their orders Paid), and a fulfilment that starts during a cancellation waits and then reads Cancelled.
    /// </summary>
    Task<EventStatus?> LockStatusAsync(Guid eventId, CancellationToken ct);
}

public enum InventoryConflictReason
{
    /// <summary>
    /// No reason given (the default of the plain constructors). A programming error: the handler rethrows it (5xx,
    /// nothing committed) and never refunds on it.
    /// </summary>
    Unknown = 0,

    /// <summary>A seat already has a non-voided ticket (<c>ux_tickets_event_seat</c>).</summary>
    SeatTaken,

    /// <summary>The general-admission tier has no capacity left (conditional <c>sold_count</c> update hit 0 rows).</summary>
    CapacityExhausted,

    /// <summary>The buyer would exceed the event's per-user ticket limit.</summary>
    PurchaseLimitExceeded,

    /// <summary>The organizer cancelled the event: no tickets, the money goes back.</summary>
    EventCancelled,

    /// <summary>The event is no longer on sale for another reason (ended).</summary>
    EventNotOnSale,
}

/// <summary>The inventory a paid order needs is not available (see <see cref="Reason"/>; deadlocks are <c>DatabaseContentionException</c>).</summary>
public sealed class InventoryConflictException : Exception
{
    public InventoryConflictException()
    {
    }

    public InventoryConflictException(string message)
        : base(message)
    {
    }

    public InventoryConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InventoryConflictException(InventoryConflictReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary><see cref="InventoryConflictReason.Unknown"/> unless a reason was given.</summary>
    public InventoryConflictReason Reason { get; } = InventoryConflictReason.Unknown;
}
