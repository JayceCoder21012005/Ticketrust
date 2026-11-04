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
/// A cancelled event stops selling at once (spec 06/07): every hold is released and every waiting room closes, which also
/// revokes outstanding admissions on their next check. Re-running is harmless (at-least-once delivery).
/// </summary>
public sealed class EventCancelledInventoryHandler(IAppDbContext db, IInventoryStore store, IConnectionMultiplexer redis, RedisKeys keys)
    : IIntegrationEventHandler<EventCancelled>
{
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

        foreach (var holdId in await store.DueHoldsAsync(message.EventId, DateTimeOffset.MaxValue, int.MaxValue, ct))
        {
            await store.ReleaseHoldAsync(message.EventId, holdId, evt.Ga, ct);
        }

        foreach (var phaseId in evt.Phases)
        {
            await QueueScripts.SetStatusAsync(redis.GetDatabase(), keys, phaseId, nameof(RoomStatus.Closed), ["*"]);
        }
    }
}
