using TicketRush.Application.Abstractions.Locking;

namespace TicketRush.Worker.Common;

/// <summary>
/// Base class for jobs that must run on exactly one worker instance (e.g. queue admission, hold expiry).
/// Instances compete for a Redis lock; the winner runs <see cref="ExecuteAsLeaderAsync"/> until it stops or
/// loses the lock, then everyone competes again. Jobs must still be idempotent: a lock can be lost
/// mid-iteration (C12).
/// </summary>
public abstract partial class LeaderElectedBackgroundService(
    IDistributedLock distributedLock,
    TimeProvider timeProvider,
    ILogger logger) : BackgroundService
{
    protected abstract string LockName { get; }

    protected virtual TimeSpan LockTtl => TimeSpan.FromSeconds(30);

    protected virtual TimeSpan RetryInterval => TimeSpan.FromSeconds(5);

    protected abstract Task ExecuteAsLeaderAsync(CancellationToken ct);

    protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneTermAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogTermFailed(logger, LockName, ex);
            }

            await Task.Delay(RetryInterval, timeProvider, stoppingToken);
        }
    }

    private async Task RunOneTermAsync(CancellationToken stoppingToken)
    {
        await using var handle = await distributedLock.TryAcquireAsync(LockName, LockTtl, stoppingToken);
        if (handle is null)
        {
            return;
        }

        LogElected(logger, LockName);
        using var term = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, handle.LockLost);
        try
        {
            await ExecuteAsLeaderAsync(term.Token);
        }
        catch (OperationCanceledException) when (handle.LockLost.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // Lost leadership; compete again on the next loop.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Became leader for {LockName}")]
    private static partial void LogElected(ILogger logger, string lockName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Leader job {LockName} failed")]
    private static partial void LogTermFailed(ILogger logger, string lockName, Exception exception);
}
