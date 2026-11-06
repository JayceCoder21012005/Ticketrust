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
            bool claimed;
            try
            {
                claimed = await tiers.TryClaimAsync(tier.Key, tier.Count(), ct);
            }
            catch (Exception ex) when (PostgresErrors.IsContention(ex))
            {
                throw new InventoryConflictException(InventoryConflictReason.Contention, "A concurrent claim on the tier collided.", ex);
            }

            if (!claimed)
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
/// <see cref="InventoryConflictReason.SeatTaken"/> conflict; a deadlock between two buyers of overlapping seats is
/// <see cref="InventoryConflictReason.Contention"/> and the caller retries.
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
        catch (DbUpdateException ex) when (PostgresErrors.IsContention(ex))
        {
            throw new InventoryConflictException(InventoryConflictReason.Contention, "A concurrent seat claim collided.", ex);
        }
    }
}

/// <summary><c>SELECT status FROM events WHERE id = @id FOR SHARE</c> on the caller's transaction.</summary>
public sealed class EventSaleLock(AppDbContext db) : IEventSaleLock
{
    public async Task<EventStatus?> LockStatusAsync(Guid eventId, CancellationToken ct)
    {
        // ToList keeps EF from wrapping the locking query in a sub-select.
        var statuses = await db.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM events WHERE id = {eventId} FOR SHARE")
            .ToListAsync(ct);
        return statuses.Count == 0 ? null : Enum.Parse<EventStatus>(statuses[0]);
    }
}

/// <summary>
/// Spec 08 step 4 / spec 12: the buyer's non-voided tickets for the event plus the new ones must stay within the
/// event's per-user limit. A transaction-scoped advisory lock per (event, buyer) serialises two fulfilments of the same
/// buyer, so both cannot pass the count before either inserts. The order does not record its sale phase, so the limit
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
