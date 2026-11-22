using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Dashboard;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Dashboard;

/// <summary>
/// Spec 15's DashboardBroadcaster: every second, while leader, one snapshot per watched event on sale (event collapsing: one per
/// second however many sales happened). Sent through the Redis backplane to the API nodes holding the connections.
/// </summary>
public sealed class DashboardBroadcaster(IServiceScopeFactory scopes, IDistributedLock distributedLock, TimeProvider time, ILogger<DashboardBroadcaster> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _clock = time;

    protected override string LockName => "dashboard-broadcaster";

    protected override TimeSpan LockTtl => TimeSpan.FromSeconds(6);

    protected override TimeSpan RetryInterval => TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<DashboardBroadcastHandler>().HandleAsync(ct);
            }

            await Task.Delay(Interval, _clock, ct);
        }
    }
}

/// <summary>Spec 15's DashboardReconciler: every minute, while leader, the live counters are rewritten from Postgres.</summary>
public sealed partial class DashboardReconciler(IServiceScopeFactory scopes, IDistributedLock distributedLock, TimeProvider time, ILogger<DashboardReconciler> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "dashboard-reconciler";

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var events = await scope.ServiceProvider.GetRequiredService<DashboardReconcileHandler>().HandleAsync(ct);
                LogReconciled(_logger, events);
            }

            await Task.Delay(Interval, _clock, ct);
        }
    }

    [LoggerMessage(EventId = 15001, Level = LogLevel.Debug, Message = "Dashboard counters of {Count} events rewritten from Postgres")]
    private static partial void LogReconciled(ILogger logger, int count);
}
