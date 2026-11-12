using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Worker.Notifications;

/// <summary>
/// Sends queued notification emails continuously. Safe to run on every worker instance at once: rows are claimed
/// with <c>FOR UPDATE SKIP LOCKED</c>, so no leader election is needed. An SMTP outage only delays emails: when a pass ends
/// because SMTP looks down, the loop pauses (5 s, doubling up to <see cref="EmailDeliveryOptions.OutageMaxBackoff"/>) instead of
/// burning the retry budget of every queued row, and goes back to normal after the first pass without an outage.
/// </summary>
public sealed partial class EmailDeliveryService(
    EmailDeliveryProcessor processor,
    IOptions<EmailDeliveryOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailDeliveryService> logger) : BackgroundService
{
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outagePause = TimeSpan.Zero;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await processor.RunBatchAsync(stoppingToken);
                outagePause = outcome.OutageSuspected ? EmailOutageBackoff.Next(outagePause, options.Value.OutageMaxBackoff) : TimeSpan.Zero;
                await Task.Delay(PauseAfter(outcome, outagePause), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Database unavailable etc. The row's transaction rolled back; nothing is lost.
                LogBatchFailed(logger, ex);
                await Task.Delay(ErrorBackoff, timeProvider, stoppingToken);
            }
        }
    }

    /// <summary>A full batch means more is probably due: loop immediately. An outage pauses longer than an idle poll.</summary>
    private TimeSpan PauseAfter(EmailBatchOutcome outcome, TimeSpan outagePause) =>
        outcome.OutageSuspected ? outagePause
        : outcome.Handled < options.Value.BatchSize ? options.Value.PollInterval
        : TimeSpan.Zero;

    [LoggerMessage(Level = LogLevel.Error, Message = "Email delivery batch failed; retrying shortly")]
    private static partial void LogBatchFailed(ILogger logger, Exception exception);
}
