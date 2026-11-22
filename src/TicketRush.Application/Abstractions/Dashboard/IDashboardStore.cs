namespace TicketRush.Application.Abstractions.Dashboard;

/// <summary>One tier's share of a paid order.</summary>
public sealed record PaidTierLine(Guid TierId, int Tickets, long Revenue);

/// <summary>A paid order as the "recent orders" table shows it.</summary>
public sealed record RecentOrder(Guid OrderId, string BuyerName, int Tickets, long Total, DateTimeOffset PaidAt);

/// <summary>The live counters of one event (spec 15). Missing fields read as 0.</summary>
public sealed record DashboardCounters(
    long Sold,
    long Revenue,
    long OrdersCreated,
    long Paid,
    long Held,
    IReadOnlyDictionary<Guid, long> SoldByTier,
    IReadOnlyDictionary<Guid, long> RevenueByTier,
    IReadOnlyList<RecentOrder> RecentOrders);

/// <summary>What Postgres says; the reconciler writes it over the live counters (holds are Redis-only and kept).</summary>
public sealed record DashboardTotals(
    long Sold,
    long Revenue,
    long OrdersCreated,
    long Paid,
    IReadOnlyDictionary<Guid, long> SoldByTier,
    IReadOnlyDictionary<Guid, long> RevenueByTier,
    IReadOnlyList<RecentOrder> RecentOrders);

/// <summary>Queue figures read from the waiting-room keys (06) of an event's phases.</summary>
public sealed record QueueFigures(long Joined, long Admitted, long Waiting, long ActiveShoppers, double AdmissionRate);

/// <summary>
/// The Redis side of the dashboard (spec 15): <c>dash:{evt}</c> counters, the recent orders list and who is watching. Writes from
/// the order path are best effort except <see cref="RecordPaidAsync"/>, which throws when Redis is down so the outbox retries.
/// </summary>
public interface IDashboardStore
{
    /// <summary>Counts a paid order once (a marker per order makes redeliveries no-ops).</summary>
    /// <returns>False when this order was already counted.</returns>
    Task<bool> RecordPaidAsync(Guid eventId, RecentOrder order, IReadOnlyList<PaidTierLine> lines, CancellationToken ct);

    /// <summary>Best effort: a hold was created.</summary>
    Task RecordHeldAsync(Guid eventId, CancellationToken ct);

    /// <summary>Best effort: a new order was created.</summary>
    Task RecordOrderCreatedAsync(Guid eventId, CancellationToken ct);

    Task<DashboardCounters> ReadAsync(Guid eventId, CancellationToken ct);

    Task OverwriteAsync(Guid eventId, DashboardTotals totals, CancellationToken ct);

    /// <param name="phases">The event's queued phases (06 keys are per phase).</param>
    Task<QueueFigures> ReadQueueAsync(IReadOnlyList<Guid> phases, DateTimeOffset now, CancellationToken ct);

    /// <summary>A connection is watching (called on join and as a heartbeat).</summary>
    Task AddViewerAsync(Guid eventId, string connectionId, DateTimeOffset now, CancellationToken ct);

    Task RemoveViewerAsync(Guid eventId, string connectionId, CancellationToken ct);

    /// <summary>Events with a viewer seen since <paramref name="seenSince"/>; stale viewers are dropped.</summary>
    Task<IReadOnlyList<Guid>> WatchedEventsAsync(DateTimeOffset seenSince, CancellationToken ct);
}
