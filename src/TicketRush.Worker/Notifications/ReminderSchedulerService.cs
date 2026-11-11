using TicketRush.Application.Abstractions.Locking;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Notifications;

/// <summary>
/// Every minute, plans the reminders followers are owed (<see cref="ReminderPlanner"/>: a sale opening within 30 minutes, an event
/// starting within 24 hours). Leader-elected so one instance ticks at a time; the planner is idempotent on its own, so a failover
/// or a lock lost mid-tick (or two instances that both think they lead) still emits each reminder exactly once (C12).
/// </summary>
public sealed partial class ReminderSchedulerService(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<ReminderSchedulerService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;
    private long _tickCount;

    protected override string LockName => "reminders";

    /// <summary>Ticks this instance has run as leader (tests use it to tell leader from follower).</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            await Task.Delay(Interval, _clock, ct);
        }
    }

    /// <returns>How many reminders this tick recorded.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var planned = await scope.ServiceProvider.GetRequiredService<ReminderPlanner>().PlanAsync(_clock.GetUtcNow(), ct);
        Interlocked.Increment(ref _tickCount);
        if (planned > 0)
        {
            LogPlanned(_logger, planned);
        }

        return planned;
    }

    [LoggerMessage(EventId = 9101, Level = LogLevel.Information, Message = "Planned {Count} reminders")]
    private static partial void LogPlanned(ILogger logger, int count);
}
