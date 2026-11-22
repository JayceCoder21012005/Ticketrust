using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Dashboard;

internal static class DashboardAccess
{
    public static Task<bool> OwnsAsync(IAppDbContext db, Guid eventId, Guid organizerId, CancellationToken ct) =>
        db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId && e.OrganizerId == organizerId, ct);
}

/// <summary>
/// Builds the snapshot of one event from the live counters (sold, revenue, funnel, recent orders), the inventory keys (held and
/// remaining per tier, 07) and the queue keys (06). An event whose inventory is not warm reads remaining as capacity − sold.
/// </summary>
public sealed class DashboardSnapshotBuilder(IAppDbContext db, IDashboardStore store, IHoldCatalog catalog, IInventoryStore inventory, TimeProvider time)
{
    public async Task<DashboardSnapshot> BuildAsync(Guid eventId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var tiers = await db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Tiers)
            .Select(t => new { t.Id, t.Name, t.Color, t.Price, t.Kind, t.Capacity })
            .ToListAsync(ct);
        var seatsPerTier = await db.EventSeats.AsNoTracking().Where(s => s.EventId == eventId)
            .GroupBy(s => s.TierId).Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.TierId, g => g.Count, ct);
        var phases = await db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Phases).Select(p => p.Id).ToListAsync(ct);

        var counters = await store.ReadAsync(eventId, ct);
        var queue = await store.ReadQueueAsync(phases, now, ct);
        var live = await LiveInventoryAsync(eventId, tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToList(), ct);

        var rows = tiers.OrderByDescending(t => t.Price.Amount).Select(t =>
        {
            var capacity = t.Kind == TicketKind.Seated ? seatsPerTier.GetValueOrDefault(t.Id) : t.Capacity ?? 0;
            var sold = counters.SoldByTier.GetValueOrDefault(t.Id);
            int held, remaining;
            if (live is null)
            {
                held = 0;
                remaining = (int)Math.Max(0, capacity - sold);
            }
            else if (t.Kind == TicketKind.Seated)
            {
                held = live.HeldSeatsByTier.GetValueOrDefault(t.Id);
                remaining = Math.Max(0, capacity - held - live.SoldSeatsByTier.GetValueOrDefault(t.Id));
            }
            else
            {
                remaining = live.GaRemaining.GetValueOrDefault(t.Id);
                held = (int)Math.Max(0, capacity - remaining - sold);
            }

            return new DashboardTier(t.Id, t.Name, t.Color, t.Price.Amount, capacity, sold, held, remaining, counters.RevenueByTier.GetValueOrDefault(t.Id));
        }).ToList();

        return new DashboardSnapshot(
            eventId,
            now,
            counters.Sold,
            counters.Revenue,
            tiers.FirstOrDefault()?.Price.Currency ?? Money.DefaultCurrency,
            rows.Sum(r => (long)r.Remaining),
            rows,
            new DashboardFunnel(queue.Joined, queue.Admitted, counters.Held, counters.OrdersCreated, counters.Paid),
            new DashboardQueue(queue.Waiting, queue.ActiveShoppers, queue.AdmissionRate),
            counters.RecentOrders);
    }

    private sealed record LiveInventory(Dictionary<Guid, int> HeldSeatsByTier, Dictionary<Guid, int> SoldSeatsByTier, IReadOnlyDictionary<Guid, int> GaRemaining);

    private async Task<LiveInventory?> LiveInventoryAsync(Guid eventId, IReadOnlyList<Guid> gaTiers, CancellationToken ct)
    {
        var snapshot = await inventory.GetSnapshotAsync(eventId, gaTiers, ct);
        if (!snapshot.Warm || await catalog.GetAsync(eventId, ct) is not { } entry)
        {
            return null;
        }

        var tierOf = entry.Seats.ToDictionary(s => s.Index, s => s.TierId);
        Dictionary<Guid, int> Count(IEnumerable<int> seats) =>
            seats.Where(tierOf.ContainsKey).GroupBy(s => tierOf[s]).ToDictionary(g => g.Key, g => g.Count());
        return new LiveInventory(Count(snapshot.Held), Count(snapshot.Sold), snapshot.GaRemaining);
    }
}

/// <summary><c>GET /organizer/events/{id}/dashboard</c>: the current snapshot (the page's first render). 404 for someone else's event.</summary>
public sealed class GetDashboardHandler(IAppDbContext db, DashboardSnapshotBuilder builder)
{
    public async Task<Result<DashboardSnapshot>> HandleAsync(Guid organizerId, Guid eventId, CancellationToken ct)
    {
        if (!await DashboardAccess.OwnsAsync(db, eventId, organizerId, ct))
        {
            return DashboardErrors.NotFound;
        }

        try
        {
            return await builder.BuildAsync(eventId, ct);
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }
}

public sealed record SalesSeriesQuery(Guid OrganizerId, Guid EventId, DateTimeOffset? From, DateTimeOffset? To, string? Bucket);

/// <summary>
/// <c>GET /organizer/events/{id}/sales-series</c>: tickets and revenue per tier per minute (<c>1m</c>, at most 24 h, default the last
/// 2 h) or per hour (<c>1h</c>, at most 90 days, default the last 7 days), from <c>sales_per_minute</c>. Buckets without sales are
/// left out; the chart fills them.
/// </summary>
public sealed class GetSalesSeriesHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result<SalesSeries>> HandleAsync(SalesSeriesQuery query, CancellationToken ct)
    {
        var bucket = query.Bucket ?? "1m";
        if (bucket is not ("1m" or "1h"))
        {
            return DashboardErrors.InvalidRange("bucket must be 1m or 1h.");
        }

        var hourly = bucket == "1h";
        var to = query.To ?? time.GetUtcNow();
        var from = query.From ?? to - (hourly ? TimeSpan.FromDays(7) : TimeSpan.FromHours(2));
        if (from >= to || to - from > (hourly ? TimeSpan.FromDays(90) : TimeSpan.FromHours(24)))
        {
            return DashboardErrors.InvalidRange(hourly ? "from must be before to, at most 90 days apart." : "from must be before to, at most 24 hours apart.");
        }

        if (!await DashboardAccess.OwnsAsync(db, query.EventId, query.OrganizerId, ct))
        {
            return DashboardErrors.NotFound;
        }

        var rows = await db.SalesPerMinute.AsNoTracking()
            .Where(s => s.EventId == query.EventId && s.Minute >= from && s.Minute < to)
            .OrderBy(s => s.Minute)
            .Select(s => new { s.Minute, s.TierId, s.Tickets, s.Revenue })
            .ToListAsync(ct);
        var points = rows
            .GroupBy(r => (At: hourly ? new DateTimeOffset(r.Minute.Year, r.Minute.Month, r.Minute.Day, r.Minute.Hour, 0, 0, TimeSpan.Zero) : r.Minute, r.TierId))
            .Select(g => new SalesPoint(g.Key.At, g.Key.TierId, g.Sum(r => r.Tickets), g.Sum(r => r.Revenue)))
            .OrderBy(p => p.At)
            .ToList();
        return new SalesSeries(bucket, from, to, points);
    }
}

public sealed record ListEventOrdersQuery(Guid OrganizerId, Guid EventId, string? Cursor, OrderStatus? Status, int Limit = 50);

/// <summary>
/// <c>GET /organizer/events/{id}/orders</c>: the event's paid primary orders (and those refunded since), newest payment first,
/// keyset-paged on (paid_at, id).
/// </summary>
public sealed class ListEventOrdersHandler(IAppDbContext db)
{
    public const int MaxLimit = 100;

    private static readonly OrderStatus[] Listed = [OrderStatus.Paid, OrderStatus.RefundPending, OrderStatus.Refunded];

    public async Task<Result<EventOrderPage>> HandleAsync(ListEventOrdersQuery query, CancellationToken ct)
    {
        if (query.Status is { } wanted && !Listed.Contains(wanted))
        {
            return DashboardErrors.InvalidRange("status must be Paid, RefundPending or Refunded.");
        }

        if (!TryDecode(query.Cursor, out var after))
        {
            return DashboardErrors.InvalidRange("Invalid cursor.");
        }

        if (!await DashboardAccess.OwnsAsync(db, query.EventId, query.OrganizerId, ct))
        {
            return DashboardErrors.NotFound;
        }

        var limit = Math.Clamp(query.Limit, 1, MaxLimit);
        var orders = db.Orders.AsNoTracking()
            .Where(o => o.EventId == query.EventId && o.Kind == OrderKind.Primary && o.PaidAt != null && Listed.Contains(o.Status));
        if (query.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (after is { } cursor)
        {
            orders = orders.Where(o => o.PaidAt < cursor.PaidAt || (o.PaidAt == cursor.PaidAt && o.Id.CompareTo(cursor.Id) < 0));
        }

        var page = await orders.OrderByDescending(o => o.PaidAt).ThenByDescending(o => o.Id).Take(limit + 1)
            .Select(o => new EventOrderItem(o.Id, o.Buyer.Name, o.Buyer.Email, o.Items.Sum(i => i.Quantity), o.Total.Amount, o.Status, o.PaidAt!.Value))
            .ToListAsync(ct);
        var next = page.Count > limit ? Encode(page[limit - 1]) : null;
        return new EventOrderPage([.. page.Take(limit)], next);
    }

    private static string Encode(EventOrderItem item) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{item.PaidAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{item.OrderId:N}"));

    private static bool TryDecode(string? cursor, out (DateTimeOffset PaidAt, Guid Id)? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return true;
        }

        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Trim())).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParse(parts[1], out var id))
            {
                value = (new DateTimeOffset(ticks, TimeSpan.Zero), id);
                return true;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}

/// <summary><c>GET /organizer/events/{id}/export.csv</c>: ownership first; the rows themselves are streamed by the endpoint.</summary>
public sealed class ExportAttendeesHandler(IAppDbContext db, IAttendeeExport export)
{
    public async Task<Result<IAsyncEnumerable<AttendeeRow>>> HandleAsync(Guid organizerId, Guid eventId, CancellationToken ct) =>
        await DashboardAccess.OwnsAsync(db, eventId, organizerId, ct)
            ? Result.Success(export.StreamAsync(eventId, ct))
            : DashboardErrors.NotFound;
}

/// <summary>
/// One tick of spec 15's DashboardBroadcaster: for each event with a live viewer that is still on sale (published, not ended),
/// build ONE snapshot and publish it, however many sales happened since the last tick.
/// </summary>
public sealed partial class DashboardBroadcastHandler(
    IAppDbContext db, IDashboardStore store, DashboardSnapshotBuilder builder, IDashboardPublisher publisher, TimeProvider time, ILogger<DashboardBroadcastHandler> logger)
{
    /// <summary>A viewer not seen (join or heartbeat) for this long is gone, e.g. its API node died.</summary>
    public static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(90);

    /// <returns>How many snapshots were sent.</returns>
    public async Task<int> HandleAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var watched = await store.WatchedEventsAsync(now - ViewerTimeout, ct);
        if (watched.Count == 0)
        {
            return 0;
        }

        var onSale = await db.Events.AsNoTracking()
            .Where(e => watched.Contains(e.Id) && e.Status == EventStatus.Published && e.EndsAt > now)
            .Select(e => e.Id)
            .ToListAsync(ct);
        var sent = 0;
        foreach (var eventId in onSale)
        {
            try
            {
                await publisher.PublishAsync(await builder.BuildAsync(eventId, ct), ct);
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One event's failure (Redis hiccup, backplane down) must not starve the others; the next tick tries again.
                LogSnapshotFailed(logger, eventId, ex);
            }
        }

        return sent;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard snapshot of event {EventId} could not be sent")]
    private static partial void LogSnapshotFailed(ILogger logger, Guid eventId, Exception exception);
}
