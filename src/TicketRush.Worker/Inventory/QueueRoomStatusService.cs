using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;
using TicketRush.Worker.Common;
using TicketRush.Worker.Queue;

namespace TicketRush.Worker.Inventory;

/// <summary>
/// Keeps each selling waiting room in step with its event's inventory (spec 06, built with 07): nothing left but holds →
/// <c>AllHeld</c> (holds may still expire), nothing left at all → <c>SoldOut</c>, anything free again → <c>Open</c>.
/// Rooms in Pre, Opening or Closed are left alone. This replaces the spec's InventoryDepleted/Restored events: the queue
/// reads only Redis, so an outbox hop would only add lag.
/// </summary>
public sealed class QueueRoomStatusService(
    LiveSales sales,
    LiveInventoryEvents events,
    IInventoryStore store,
    IConnectionMultiplexer redis,
    RedisKeys keys,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<QueueRoomStatusService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    private static readonly string[] SellingStatuses = [nameof(RoomStatus.Open), nameof(RoomStatus.AllHeld), nameof(RoomStatus.SoldOut)];

    private readonly TimeProvider _clock = time;

    protected override string LockName => "queue-room-status";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(6);

    protected override TimeSpan RetryInterval => TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(1), _clock, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var gaTiers = (await events.GetAsync(ct)).ToDictionary(e => e.EventId, e => e.GaTierIds);
        var db = redis.GetDatabase();
        foreach (var sale in await sales.GetAsync(ct))
        {
            if (now < sale.OpensAt || now >= sale.ClosesAt)
            {
                continue;
            }

            var counts = await store.GetCountsAsync(sale.EventId, gaTiers.GetValueOrDefault(sale.EventId) ?? [], ct);
            if (counts.Warm)
            {
                await QueueScripts.SetStatusAsync(db, keys, sale.PhaseId, Target(counts).ToString(), SellingStatuses);
            }
        }
    }

    private static RoomStatus Target(InventoryCounts counts) => counts switch
    {
        { Available: > 0 } => RoomStatus.Open,
        { Held: > 0 } => RoomStatus.AllHeld,
        _ => RoomStatus.SoldOut,
    };
}
