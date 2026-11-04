using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// A cancelled event stops selling at once (spec 06/07, follow-up A2). Order matters: first every waiting room closes and
/// its admissions are revoked (nobody new can reach the buy path, so no hold can appear while we clean up), then every
/// hold is released in batches. Each step is idempotent and the handler does as much as it can before it reports a
/// failure, so at-least-once redelivery simply finishes a half-done run.
/// </summary>
public sealed class EventCancelledInventoryHandler(IAppDbContext db, IInventoryStore store, IConnectionMultiplexer redis, RedisKeys keys)
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
        await ReleaseHoldsAsync(message.EventId, evt.Ga, failures, ct);

        // EXTENSION POINT (spec 08 task B9): open orders -> Cancelled and paid orders -> RefundPending go here, strictly
        // after the rooms are closed and the holds released (same rule: stop selling first, then settle). They must be
        // batched, re-run safe and add their own failures to `failures`. No order code lives here yet.
        if (failures.Count > 0)
        {
            throw new AggregateException("Cancelling the event's inventory did not finish; it will be retried.", failures);
        }
    }

    /// <summary>Closes each room and drops its admissions: an admitted shopper's token stops passing the buy-path check.</summary>
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
}
