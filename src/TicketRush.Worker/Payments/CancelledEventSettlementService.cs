using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Worker.Common;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Backstop for cancelled events (spec 08, B9): normally the <c>EventCancelled</c> outbox message settles a cancelled event's
/// orders within seconds, but a message the outbox dead-lettered (e.g. a database outage longer than its retries) never runs
/// again. Every <see cref="CancelledEventSettlementOptions.Interval"/> this pass lists Cancelled events that still have Pending
/// or Paid orders (one query per page; an event with nothing left costs nothing more) and runs
/// <see cref="CancelEventOrdersHandler"/> for each. That handler is re-run safe and serialised with the outbox's run by the
/// exclusive event sale lock, so overlapping with a live message is harmless. Each event gets its own scope and failure
/// handling: one bad event is logged, counted and retried next pass. Leader-elected.
/// </summary>
public sealed partial class CancelledEventSettlementService(
    IServiceScopeFactory scopes,
    PaymentWorkerMetrics metrics,
    EventCancellationMetrics settlements,
    IDistributedLock distributedLock,
    IOptions<CancelledEventSettlementOptions> options,
    TimeProvider time,
    ILogger<CancelledEventSettlementService> logger)
    : LeaderElectedBackgroundService(distributedLock, time, logger)
{
    public const int MaxPagesPerPass = 10;

    private readonly TimeProvider _clock = time;
    private readonly ILogger _logger = logger;

    protected override string LockName => "cancelled-event-settlement";

    protected override async Task ExecuteAsLeaderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RunOnceAsync(ct);
            await Task.Delay(options.Value.Interval, _clock, ct);
        }
    }

    /// <returns>How many cancelled events with orders left to settle this pass found.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var pageSize = options.Value.EventsPerPage;
        Guid? after = null;
        var seen = 0;
        for (var page = 0; page < MaxPagesPerPass; page++)
        {
            IReadOnlyList<Guid> events;
            await using (var scope = scopes.CreateAsyncScope())
            {
                events = await scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>().ListUnsettledEventsAsync(after, pageSize, ct);
            }

            foreach (var eventId in events)
            {
                ct.ThrowIfCancellationRequested();
                await SettleAsync(eventId, ct);
            }

            seen += events.Count;
            if (events.Count < pageSize)
            {
                break;
            }

            after = events[^1];
        }

        return seen;
    }

    private async Task SettleAsync(Guid eventId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var settled = await scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>().HandleAsync(eventId, settlements.Record, ct);
            metrics.CancelledEventSweep(settled.Orders > 0 ? "settled" : "unchanged");
            if (settled.Orders > 0)
            {
                // Work the outbox message should have done: worth a look at why it did not.
                LogSettled(_logger, eventId, settled.Cancelled, settled.RefundPending);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailed(_logger, eventId, ex);
            metrics.CancelledEventSweep("error");
        }
    }

    [LoggerMessage(EventId = 8306, Level = LogLevel.Warning, Message = "Backstop settled cancelled event {EventId}: {Cancelled} orders cancelled, {RefundPending} sent to refund (its EventCancelled message did not).")]
    private static partial void LogSettled(ILogger logger, Guid eventId, int cancelled, int refundPending);

    [LoggerMessage(EventId = 8307, Level = LogLevel.Error, Message = "Backstop settlement failed for cancelled event {EventId}; retried on the next pass")]
    private static partial void LogFailed(ILogger logger, Guid eventId, Exception exception);
}
