using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Orders;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Spec 08 RefundWorker: every 5 s, sends back the money of every order and payment session in <c>RefundPending</c>
/// (<see cref="ProcessRefundHandler"/>), oldest first, at most <see cref="BatchSize"/> per pass. It reads the database
/// state, not the outbox, so a refund is retried until it is confirmed or parked for a person, never dead-lettered and
/// forgotten. Leader-elected to avoid needless double calls, but safe on two nodes at once: the gateway idempotency key
/// makes the refund happen once, and <c>xmin</c> lets only one node commit it. After each pass the parked count feeds the
/// <c>ticketrush.payments.refunds_parked</c> gauge.
/// </summary>
public sealed partial class RefundWorker(
    IServiceScopeFactory scopes,
    PaymentWorkerMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<RefundWorker> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public const int BatchSize = 50;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;
    private long _tickCount;

    protected override string LockName => "refund-worker";

    /// <summary>Passes this instance has run (tests use it to tell leader from follower).</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            await Task.Delay(Interval, _clock, ct);
        }
    }

    /// <returns>How many due refunds this pass tried.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<RefundWorkItem> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            due = await scope.ServiceProvider.GetRequiredService<ProcessRefundHandler>().ListDueAsync(_clock.GetUtcNow(), BatchSize, ct);
        }

        foreach (var item in due)
        {
            await ProcessAsync(item, unavailable, ct);
        }

        await using (var scope = scopes.CreateAsyncScope())
        {
            metrics.SetParked(await scope.ServiceProvider.GetRequiredService<ProcessRefundHandler>().CountParkedAsync(ct));
        }

        Interlocked.Increment(ref _tickCount);
        return due.Count;
    }

    private async Task ProcessAsync(RefundWorkItem item, ISet<string> unavailable, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ProcessRefundHandler>().HandleAsync(item, unavailable, ct);
            metrics.Refund(item.Target, outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Nothing was committed for this refund; it is still RefundPending and comes back on the next pass.
            LogRefundFailed(_logger, item.Target, item.Id, ex);
            metrics.RefundFailed(item.Target);
        }
    }

    [LoggerMessage(EventId = 8216, Level = LogLevel.Error, Message = "Refund of {Target} {Id} failed; retried on the next pass")]
    private static partial void LogRefundFailed(ILogger logger, RefundTarget target, Guid id, Exception exception);
}
