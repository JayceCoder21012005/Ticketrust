using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Infrastructure.Queue;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Queue;

/// <summary>
/// Every second: expires finished shoppers and admits the next buyers of each open sale; closes sales past
/// <c>closesAt</c> (spec 06). Leader-elected, so the admission rate is global, not per instance.
/// </summary>
public sealed class QueueAdmitterService(
    LiveSales sales,
    WaitingRoomAdmitter admitter,
    ISalesMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<QueueAdmitterService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    private readonly TimeProvider _clock = time;

    private long _tickCount;

    protected override string LockName => "queue-admitter";

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
        foreach (var sale in await sales.GetAsync(ct))
        {
            if (now >= sale.ClosesAt)
            {
                await admitter.CloseAsync(sale.PhaseId, ct);
                metrics.QueueClosed(sale.PhaseId);
            }
            else if (now >= sale.OpensAt)
            {
                var tick = await admitter.TickAsync(sale.PhaseId, ct);
                metrics.Admitted((int)(tick.AdmittedAfter - tick.AdmittedBefore));
                metrics.QueueObserved(sale.PhaseId, tick.Seq - tick.AdmittedAfter, tick.ActiveCount);
            }
        }

        Interlocked.Increment(ref _tickCount);
    }
}
