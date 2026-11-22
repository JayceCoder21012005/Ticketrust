using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Inventory;

/// <summary>
/// Keeps Redis inventory in line with the database (spec 07, C12): every 5 s it warms live events that are still cold
/// (e.g. after a Redis restart), and on start and every 5 min it reconciles every live event and logs the drift it fixed.
/// </summary>
public sealed partial class InventoryReconcilerService(
    LiveInventoryEvents live,
    IInventoryStore store,
    IServiceScopeFactory scopes,
    ISalesMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<InventoryReconcilerService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    private static readonly TimeSpan QuickEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FullEvery = TimeSpan.FromMinutes(5);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "inventory-reconciler";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        var lastFull = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            var full = _clock.GetUtcNow() - lastFull >= FullEvery;
            await RunOnceAsync(full, ct);
            if (full)
            {
                lastFull = _clock.GetUtcNow();
            }

            await Task.Delay(QuickEvery, _clock, ct);
        }
    }

    /// <param name="full">Reconcile every live event; otherwise only seed the ones that are not warm.</param>
    public async Task RunOnceAsync(bool full, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<InventorySeeder>();
        foreach (var evt in await live.GetAsync(ct))
        {
            if (!full && await store.IsWarmAsync(evt.EventId, ct))
            {
                continue;
            }

            // Only a full pass over a warm event finds drift; seeding a cold one (after a Redis restart) is a rebuild, not a fix.
            if (await seeder.SeedAsync(evt.EventId, ct) is { HadDrift: true } report && full)
            {
                LogDrift(_logger, evt.EventId, report.SoldAdded, report.SoldRemoved, report.GaFixed.Count);
                metrics.DriftFixed(report.SoldAdded + report.SoldRemoved + report.GaFixed.Count);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory drift fixed for event {EventId}: sold +{SoldAdded} -{SoldRemoved}, GA tiers fixed {GaTiers}")]
    private static partial void LogDrift(ILogger logger, Guid eventId, int soldAdded, int soldRemoved, int gaTiers);
}
