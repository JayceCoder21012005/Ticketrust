using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Orders;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Spec 08 OrderExpiryService: every 5 s, open orders past their expiry are asked about at the gateway first and then
/// expired, paid late or sent to refund (<see cref="ExpireOrderHandler"/>). Pages of <see cref="BatchSize"/> by expiry
/// (index <c>ix_orders_open_expires_at</c>), up to <see cref="MaxPagesPerPass"/> pages per pass, so orders skipped for a
/// gateway outage never hide the ones behind them. Each order gets its own scope and its own failure handling: one bad
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

    /// <returns>How many due orders this pass looked at.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var unavailable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExpiryCursor? cursor = null;
        var seen = 0;
        for (var page = 0; page < MaxPagesPerPass; page++)
        {
            var due = await ListDueAsync(now, cursor, ct);
            foreach (var orderId in due.OrderIds)
            {
                await ProcessAsync(orderId, unavailable, ct);
            }

            seen += due.OrderIds.Count;
            if (due.OrderIds.Count < BatchSize)
            {
                break;
            }

            cursor = due.Next;
        }

        Interlocked.Increment(ref _tickCount);
        return seen;
    }

    private async Task<DueOrders> ListDueAsync(DateTimeOffset now, ExpiryCursor? cursor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>().ListDueAsync(now, cursor, BatchSize, ct);
    }

    private async Task ProcessAsync(Guid orderId, ISet<string> unavailable, CancellationToken ct)
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

    [LoggerMessage(EventId = 8205, Level = LogLevel.Error, Message = "Expiry sweep failed for order {OrderId}; retried on the next pass")]
    private static partial void LogOrderFailed(ILogger logger, Guid orderId, Exception exception);
}
