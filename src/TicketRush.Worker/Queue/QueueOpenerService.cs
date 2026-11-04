using TicketRush.Application.Abstractions.Locking;
using TicketRush.Infrastructure.Queue;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Queue;

/// <summary>
/// At <c>opensAt</c> shuffles the pre-queue and opens the room (spec 06). Leader-elected: exactly one instance runs the
/// shuffle; if it dies mid-way the next leader resumes (the opener is idempotent).
/// </summary>
public sealed class QueueOpenerService(
    LiveSales sales,
    WaitingRoomOpener opener,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<QueueOpenerService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    private readonly TimeProvider _clock = time;

    protected override string LockName => "queue-opener";

    // Short TTL + fast retry: a crashed leader is replaced well inside 10 s (spec 06 acceptance).
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
        foreach (var sale in await sales.GetAsync(ct))
        {
            if (sale.OpensAt <= now && now < sale.ClosesAt)
            {
                await opener.OpenAsync(sale.PhaseId, ct);
            }
        }
    }
}
