using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Inventory;

/// <summary>
/// Every second, releases the holds of live events whose expiry has passed (spec 07: seats free within 2 s of
/// <c>expiresAt</c>). Leader-elected, so one sweeper runs at a time; releasing is idempotent, so a failover mid-sweep is safe.
/// </summary>
public sealed class HoldExpiryService(
    LiveInventoryEvents live,
    IInventoryStore store,
    ISalesMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<HoldExpiryService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    /// <summary>Most holds released per event per pass; the rest wait one second.</summary>
    public const int BatchSize = 500;

    private readonly TimeProvider _clock = time;

    private long _tickCount;

    protected override string LockName => "hold-expiry";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(6);

    protected override TimeSpan RetryInterval => TimeSpan.FromSeconds(1);

    /// <summary>Passes this instance has run as leader (tests use it to tell leader from follower).</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

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
        foreach (var evt in await live.GetAsync(ct))
        {
            var released = 0;
            foreach (var holdId in await store.DueHoldsAsync(evt.EventId, now, BatchSize, ct))
            {
                if (await store.ReleaseHoldAsync(evt.EventId, holdId, evt.GaTierIds, ct))
                {
                    released++;
                }
            }

            metrics.HoldsExpired(released);
        }

        Interlocked.Increment(ref _tickCount);
    }
}
