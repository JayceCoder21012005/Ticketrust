using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Waitlist;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Waitlist;

/// <summary>
/// Spec 13: once a second, unused waitlist offers miss and their holds move to the next waiter (or back on sale), so a pass happens
/// within 5 s of expiry. Waitlist holds are never in the expiry ZSET, so only this loop ends them. Leader-elected; each pass is
/// one atomic Redis step plus a save per entry, and a retry after a lost lock finds the hold it already passed (C12).
/// </summary>
public sealed partial class WaitlistOfferService(
    IServiceScopeFactory scopes,
    IDistributedLock distributedLock,
    TimeProvider time,
    ILogger<WaitlistOfferService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "waitlist-offers";

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await using (var scope = scopes.CreateAsyncScope())
            {
                var handled = await scope.ServiceProvider.GetRequiredService<WaitlistOffers>().ExpireDueAsync(ct);
                if (handled > 0)
                {
                    LogPassed(_logger, handled);
                }
            }

            await Task.Delay(Interval, _clock, ct);
        }
    }

    [LoggerMessage(EventId = 13001, Level = LogLevel.Information, Message = "Moved {Count} expired waitlist offers on")]
    private static partial void LogPassed(ILogger logger, int count);
}
