using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;

namespace TicketRush.Application.Features.Dashboard;

/// <summary>
/// Spec 15's DashboardReconciler: recomputes the live counters of every event that is selling (or ended within the last day) from
/// Postgres, the truth, and writes them over <c>dash:{evt}</c>. Counts <see cref="OrderStatus.Paid"/> primary orders only, so a
/// refunded order drops out here. Holds are known only to Redis and are kept.
/// </summary>
public sealed class DashboardReconcileHandler(IAppDbContext db, IDashboardStore store, TimeProvider time)
{
    public static readonly TimeSpan EndedWindow = TimeSpan.FromDays(1);

    /// <returns>How many events were rewritten.</returns>
    public async Task<int> HandleAsync(CancellationToken ct)
    {
        var since = time.GetUtcNow() - EndedWindow;
        var events = await db.Events.AsNoTracking()
            .Where(e => (e.Status == EventStatus.Published || e.Status == EventStatus.Ended) && e.EndsAt > since)
            .Select(e => e.Id)
            .ToListAsync(ct);
        foreach (var eventId in events)
        {
            await store.OverwriteAsync(eventId, await TotalsAsync(db, eventId, ct), ct);
        }

        return events.Count;
    }

    /// <summary>The dashboard's numbers as Postgres has them.</summary>
    public static async Task<DashboardTotals> TotalsAsync(IAppDbContext db, Guid eventId, CancellationToken ct)
    {
        var primary = db.Orders.AsNoTracking().Where(o => o.EventId == eventId && o.Kind == OrderKind.Primary);
        var paid = primary.Where(o => o.Status == OrderStatus.Paid);
        var tiers = await paid.SelectMany(o => o.Items)
            .GroupBy(i => i.TierId)
            .Select(g => new { TierId = g.Key, Sold = g.Sum(i => (long)i.Quantity), Revenue = g.Sum(i => i.UnitPrice.Amount * i.Quantity) })
            .ToListAsync(ct);
        var recent = await paid.OrderByDescending(o => o.PaidAt).ThenByDescending(o => o.Id).Take(20)
            .Select(o => new { o.Id, o.Buyer.Name, Tickets = o.Items.Sum(i => i.Quantity), o.Total.Amount, o.PaidAt })
            .ToListAsync(ct);
        return new DashboardTotals(
            tiers.Sum(t => t.Sold),
            tiers.Sum(t => t.Revenue),
            await primary.LongCountAsync(ct),
            await paid.LongCountAsync(ct),
            tiers.ToDictionary(t => t.TierId, t => t.Sold),
            tiers.ToDictionary(t => t.TierId, t => t.Revenue),
            [.. recent.Select(o => new RecentOrder(o.Id, o.Name, o.Tickets, o.Amount, o.PaidAt!.Value))]);
    }
}
