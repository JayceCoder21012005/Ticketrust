using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Worker.Outbox;

/// <summary>
/// Drains the outbox continuously. Safe to run on every worker instance at once: rows are claimed
/// with <c>FOR UPDATE SKIP LOCKED</c>, so no leader election is needed.
/// </summary>
public sealed partial class OutboxDispatcherService(
    OutboxProcessor processor,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxDispatcherService> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await processor.ProcessBatchAsync(stoppingToken);
                // A full batch means there is probably more waiting: loop immediately.
                if (claimed < options.Value.BatchSize)
                {
                    await Task.Delay(options.Value.PollInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unavailable etc. The batch transaction rolled back; nothing is lost.
                LogBatchFailed(logger, ex);
                await Task.Delay(ErrorBackoff, timeProvider, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox batch failed; retrying shortly")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
