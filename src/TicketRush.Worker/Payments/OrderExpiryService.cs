using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Orders;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Spec 08 OrderExpiryService: every 5 s, open orders past their expiry are asked about at the gateway first and then
/// expired, paid late or sent to refund (<see cref="ExpireOrderHandler"/>). Pages of <see cref="BatchSize"/> by expiry
/// (index <c>ix_orders_open_expires_at</c>), up to <see cref="MaxPagesPerPass"/> pages per pass, so orders skipped for a
/// gateway outage never hide the ones behind them. After the orders, the same pass pages through stale Pending sessions of
/// orders that are no longer open (<see cref="ExpireOrderHandler.ReconcileSessionAsync"/>), so money that reached a session
/// after its order closed is still found. Each order gets its own scope and its own failure handling: one bad
/// order is logged, counted and retried next pass, never stopping the rest. Leader-elected; a failover mid-pass is safe
/// because every decision is guarded by the rows' <c>xmin</c>.
/// </summary>
public sealed partial class OrderExpiryService(
    IServiceScopeFactory scopes,
    PaymentWorkerMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<OrderExpiryService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public const int BatchSize = 100;
    public const int MaxPagesPerPass = 10;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;
    private long _tickCount;

    protected override string LockName => "order-expiry";

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

    /// <returns>How many due orders and stale sessions this pass looked at.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = await PagesAsync(
            (handler, cursor) => handler.ListDueAsync(now, cursor, BatchSize, ct),
            id => ProcessOrderAsync(id, unavailable, ct),
            ct);
        seen += await PagesAsync(
            (handler, cursor) => handler.ListStaleSessionsAsync(now, cursor, BatchSize, ct),
            id => ProcessSessionAsync(id, unavailable, ct),
            ct);
        Interlocked.Increment(ref _tickCount);
        return seen;
    }

    /// <summary>Up to <see cref="MaxPagesPerPass"/> pages of <see cref="BatchSize"/>; skipped items never hide the ones behind them.</summary>
    private async Task<int> PagesAsync(
        Func<ExpireOrderHandler, ExpiryCursor?, Task<DueOrders>> list, Func<Guid, Task> process, CancellationToken ct)
    {
        ExpiryCursor? cursor = null;
        var seen = 0;
        for (var page = 0; page < MaxPagesPerPass; page++)
        {
            DueOrders due;
            await using (var scope = scopes.CreateAsyncScope())
            {
                due = await list(scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>(), cursor);
            }

            foreach (var id in due.OrderIds)
            {
                ct.ThrowIfCancellationRequested();
                await process(id);
            }

            seen += due.OrderIds.Count;
            if (due.OrderIds.Count < BatchSize)
            {
                break;
            }

            cursor = due.Next;
        }

        return seen;
    }

    private async Task ProcessOrderAsync(Guid orderId, ISet<string> unavailable, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>().HandleAsync(orderId, unavailable, ct);
            metrics.Expiry(outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Lock timeout, a conflict on every try, a missing gateway: this order waits for the next pass, the others go on.
            LogOrderFailed(_logger, orderId, ex);
            metrics.ExpiryFailed();
        }
    }

    private async Task ProcessSessionAsync(Guid attemptId, ISet<string> unavailable, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>().ReconcileSessionAsync(attemptId, unavailable, ct);
            metrics.Session(outcome);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogSessionFailed(_logger, attemptId, ex);
            metrics.SessionFailed();
        }
    }

    [LoggerMessage(EventId = 8207, Level = LogLevel.Error, Message = "Session reconcile failed for payment attempt {AttemptId}; retried on the next pass")]
    private static partial void LogSessionFailed(ILogger logger, Guid attemptId, Exception exception);

    [LoggerMessage(EventId = 8205, Level = LogLevel.Error, Message = "Expiry sweep failed for order {OrderId}; retried on the next pass")]
    private static partial void LogOrderFailed(ILogger logger, Guid orderId, Exception exception);
}
