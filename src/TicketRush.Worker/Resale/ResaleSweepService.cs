using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Resale;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Resale;

/// <summary>
/// Spec 14, every 30 s while leader: pending transfers expire after 48 h, listings and transfers end at the 2-hour cutoff (the
/// owner keeps a usable ticket), and payouts of ended events are paid (mock). Each item is saved on its own, so a crash or a lost
/// lock only delays the rest.
/// </summary>
public sealed partial class ResaleSweepService(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<ResaleSweepService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "resale-sweep";

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var changed = await scope.ServiceProvider.GetRequiredService<ResaleSweepHandler>().HandleAsync(ct);
                if (changed > 0)
                {
                    LogSwept(_logger, changed);
                }
            }

            await Task.Delay(Interval, _clock, ct);
        }
    }

    [LoggerMessage(EventId = 14001, Level = LogLevel.Information, Message = "Resale sweep changed {Count} transfers, listings or payouts")]
    private static partial void LogSwept(ILogger logger, int count);
}
