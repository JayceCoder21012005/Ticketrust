using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// A cancelled event stops selling at once (spec 06/07, follow-up A2), then settles its orders (spec 08, task B9). Order
/// matters: first every waiting room closes and its admissions are revoked (nobody new can reach the buy path, so no hold
/// can appear while we clean up), then every hold is released in batches. If any room could not be closed, no hold is
/// released in that run (releasing while a room is still open would re-open the window we are closing). The orders are then
/// settled in EVERY run (<see cref="CancelEventOrdersHandler"/>: Paid → RefundPending with its tickets voided, Pending →
/// Cancelled): that needs only the database and the event sale lock, so a Redis failure never keeps buyers' money. Every
/// failure is collected and thrown at the end, and redelivery tries again. Each step is idempotent, so at-least-once
/// redelivery simply finishes a half-done run.
/// <para>
/// One handler, not two: the outbox stops a message's handlers at the first one that throws and dead-letters the message
/// as a whole, so a second handler registered after this one would not run in any failing run. Folding the steps into one
/// handler with per-step failure collection lets the order settlement run despite a room or hold failure.
/// </para>
/// <para>
/// The order settlement runs in its own DI scope, hence on its own connection with its own short transactions (one per
/// batch of 500, each under the exclusive event sale lock). This handler itself runs inside the outbox's batch transaction
/// (a savepoint); settling there would hold the sale lock and every order row until the whole outbox batch commits, and a
/// failure would roll back batches already done. Committed batches stay committed if the outbox transaction rolls back:
/// the re-run skips them.
/// </para>
/// <para>
/// Known limitation: retries are finite. The outbox retries a failing message <c>OutboxRetryPolicy.MaxAttempts</c> (10) times
/// with exponential backoff (about 4-8 minutes in total), then dead-letters it and never runs it again, and nothing else
/// sweeps a cancelled event's holds (the hold-expiry sweep only covers Published events). A Redis outage longer than that
/// leaves holds and rooms orphaned until an operator re-drives the message. Orders are not left behind: they are settled in
/// every run, and the worker's backstop pass (<c>CancelledEventSettlementService</c>) settles any cancelled event that still
/// has Pending or Paid orders (a database outage that outlasted the retries). That is not silent: the processor logs
/// "Outbox message ... dead-lettered" at Error and increments <c>ticketrush.outbox.dead_lettered</c> tagged with this
/// message's type, which is the alert to act on.
/// </para>
/// </summary>
public sealed partial class EventCancelledInventoryHandler(
    IAppDbContext db,
    IInventoryStore store,
    IConnectionMultiplexer redis,
    RedisKeys keys,
    IServiceScopeFactory scopes,
    EventCancellationMetrics metrics,
    ILogger<EventCancelledInventoryHandler> logger)
    : IIntegrationEventHandler<EventCancelled>
{
    /// <summary>Most holds fetched and released per round, so a cancelled sell-out never loads every hold at once.</summary>
    public const int BatchSize = 500;

    public async Task HandleAsync(EventCancelled message, IntegrationEventContext context, CancellationToken ct)
    {
        var evt = await db.Events.AsNoTracking()
            .Where(e => e.Id == message.EventId)
            .Select(e => new
            {
                Ga = e.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToList(),
                Phases = e.Phases.Select(p => p.Id).ToList(),
            })
            .SingleOrDefaultAsync(ct);
        if (evt is null)
        {
            return;
        }

        var failures = new List<Exception>();
        await CloseRoomsAsync(evt.Phases, failures);
        if (failures.Count == 0)
        {
            await ReleaseHoldsAsync(message.EventId, evt.Ga, failures, ct);
        }

        // Needs only the database and the sale lock: never held back by a Redis failure (fulfilments read the status under
        // the lock, so an open room cannot turn into a Paid order of a cancelled event).
        await SettleOrdersAsync(message.EventId, failures, ct);

        if (failures.Count > 0)
        {
            throw new AggregateException("Cancelling the event's inventory did not finish; it will be retried.", failures);
        }
    }

    /// <summary>
    /// Closes each room and drops its admissions: an admitted shopper's token stops passing the buy-path check. Keeps going
    /// past a failed room so the others are closed too, but reports every failure.
    /// </summary>
    private async Task CloseRoomsAsync(IReadOnlyList<Guid> phaseIds, List<Exception> failures)
    {
        var cache = redis.GetDatabase();
        foreach (var phaseId in phaseIds)
        {
            try
            {
                // Status first: from then on no tick or status poll admits anyone, so clearing the active set sticks.
                await QueueScripts.SetStatusAsync(cache, keys, phaseId, nameof(RoomStatus.Closed), ["*"]);
                await cache.KeyDeleteAsync([keys.QueueActive(phaseId), keys.QueueTokens(phaseId)]);
            }
            catch (Exception ex) when (ex is RedisException or TimeoutException)
            {
                failures.Add(ex);
            }
        }
    }

    /// <summary>
    /// Settles the orders in a scope of its own (own connection, own per-batch transactions, see the class remarks). Batches
    /// committed before a failure stay committed and are counted; the failure is collected for the retry.
    /// </summary>
    private async Task SettleOrdersAsync(Guid eventId, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var orders = scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>();
            await orders.HandleAsync(eventId, metrics.Record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LogSettlementFailed(eventId, ex);
            failures.Add(ex);
        }
    }

    /// <summary>Releases until none are left. A failed release stops the loop (it would be offered again at once).</summary>
    private async Task ReleaseHoldsAsync(Guid eventId, IReadOnlyList<Guid> gaTiers, List<Exception> failures, CancellationToken ct)
    {
        try
        {
            while (await store.DueHoldsAsync(eventId, DateTimeOffset.MaxValue, BatchSize, ct) is { Count: > 0 } batch)
            {
                foreach (var holdId in batch)
                {
                    await store.ReleaseHoldAsync(eventId, holdId, gaTiers, ct);
                }
            }
        }
        catch (InventoryUnavailableException ex)
        {
            failures.Add(ex);
        }
    }

    [LoggerMessage(EventId = 8305, Level = LogLevel.Error, Message = "Cancelled event {EventId}: settling its orders failed; the message is retried and the re-run finishes what is left.")]
    private partial void LogSettlementFailed(Guid eventId, Exception exception);
}
