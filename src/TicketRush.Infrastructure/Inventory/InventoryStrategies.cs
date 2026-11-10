using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Configurations;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// General admission (spec 08): one conditional <c>sold_count</c> update per tier (<see cref="ITierInventory.TryClaimAsync"/>),
/// tiers in id order so concurrent fulfilments lock them in the same order, then the tickets join the unit of work.
/// 0 rows updated = sold out (or not a GA tier) = conflict. Runs first (<see cref="Order"/>).
/// </summary>
public sealed class GeneralAdmissionInventoryStrategy(ITierInventory tiers, AppDbContext db) : IInventoryStrategy
{
    public int Order => 0;

    public bool Handles(Ticket ticket) => ticket.SeatIndex is null;

    public async Task ClaimAsync(IReadOnlyList<Ticket> tickets, CancellationToken ct)
    {
        foreach (var tier in tickets.GroupBy(t => t.TierId).OrderBy(g => g.Key))
        {
            if (!await tiers.TryClaimAsync(tier.Key, tier.Count(), ct))
            {
                throw new InventoryConflictException(InventoryConflictReason.CapacityExhausted, $"Tier {tier.Key} has no capacity left for {tier.Count()} tickets.");
            }
        }

        db.Tickets.AddRange(tickets);
    }
}

/// <summary>
/// Seats (spec 08): the tickets are inserted right away, in seat order, so the partial unique index
/// <c>ux_tickets_event_seat</c> decides now whether a seat is still free. Its violation (and only its) becomes a
/// <see cref="InventoryConflictReason.SeatTaken"/> conflict; a deadlock between two buyers of overlapping seats surfaces
/// from the transaction as <c>DatabaseContentionException</c> and the caller retries.
/// </summary>
public sealed class SeatedInventoryStrategy(AppDbContext db) : IInventoryStrategy
{
    public int Order => 1;

    public bool Handles(Ticket ticket) => ticket.SeatIndex is not null;

    public async Task ClaimAsync(IReadOnlyList<Ticket> tickets, CancellationToken ct)
    {
        db.Tickets.AddRange(tickets.OrderBy(t => t.SeatIndex));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (PostgresErrors.IsUniqueViolation(ex, TicketConfiguration.SeatIndexName))
        {
            throw new InventoryConflictException(InventoryConflictReason.SeatTaken, "A seat of the order already has a ticket.", ex);
        }
    }
}

/// <summary>
/// The shared <see cref="EventSaleLocks"/> advisory lock, then a plain read of the status. The event row itself is never
/// locked, so in-flight fulfilments do not contend on the hot tuple or block organizer edits; only a cancel/end that takes
/// the exclusive variant waits for them (and they for it, bounded by the transaction's lock_timeout).
/// </summary>
public sealed class EventSaleLock(AppDbContext db) : IEventSaleLock
{
    public async Task<EventStatus?> LockStatusAsync(Guid eventId, CancellationToken ct)
    {
        await EventSaleLocks.AcquireSharedAsync(db, eventId, ct);
        // Read after the lock: a cancel that held the exclusive lock has committed by now, and READ COMMITTED sees it.
        return await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => (EventStatus?)e.Status).FirstOrDefaultAsync(ct);
    }

    public Task AcquireExclusiveAsync(Guid eventId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
        {
            // A transaction-scoped advisory lock outside a transaction would be released at once: a silent no-op.
            throw new InvalidOperationException("The exclusive sale lock needs an open transaction.");
        }

        return EventSaleLocks.AcquireExclusiveAsync(db, eventId, ct);
    }
}

/// <summary>
/// Spec 08 step 4 / spec 12: the buyer's non-voided tickets for the event plus the new ones must stay within the
/// event's per-user limit. A transaction-scoped advisory lock per (event, buyer) serialises two fulfilments of the same
/// buyer, so both cannot pass the count before either inserts (key namespace <c>ticket-limit:</c>, distinct from <see cref="EventSaleLocks"/>). The order does not record its sale phase, so the limit
/// is the most generous one of the event's phases (the hold already enforced the phase's own limit in Redis); an event
/// without phases falls back to <see cref="SalePhase.MaxTicketsPerUserLimit"/>.
/// </summary>
public sealed class TicketPurchaseLimit(AppDbContext db) : ITicketPurchaseLimit
{
    public async Task<bool> AllowsAsync(Guid eventId, Guid userId, int adding, CancellationToken ct)
    {
        var key = $"ticket-limit:{eventId:N}:{userId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", ct);

        var limit = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .SelectMany(e => e.Phases)
            .MaxAsync(p => (int?)p.MaxTicketsPerUser, ct) ?? SalePhase.MaxTicketsPerUserLimit;
        var owned = await db.Tickets.CountAsync(t => t.EventId == eventId && t.OwnerUserId == userId && t.Status != TicketStatus.Voided, ct);
        return owned + adding <= limit;
    }
}
