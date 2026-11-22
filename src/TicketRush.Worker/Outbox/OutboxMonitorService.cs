using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Abstractions.Observability;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Outbox;

/// <summary>
/// Every 15 s, measures the outbox lag and the dead letters for the <c>outbox.lag_seconds</c> and <c>outbox.dead_letters</c>
/// gauges (spec 17). Leader-elected, so one instance reports them and the alert does not see a doubled count.
/// </summary>
public sealed partial class OutboxMonitorService(
    IServiceScopeFactory scopes,
    ISalesMetrics metrics,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<OutboxMonitorService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "outbox-monitor";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            await Task.Delay(Every, _clock, ct);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (lag, dead) = await scope.ServiceProvider.GetRequiredService<OutboxStats>().ReadAsync(ct);
            metrics.OutboxObserved(lag, dead);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database is down: the gauges keep their last values and the next pass tries again.
            LogFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not measure the outbox; keeping the last values")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
