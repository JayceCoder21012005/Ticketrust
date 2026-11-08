using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Orders;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// <c>OrderPaid</c> consumer (spec 08 / 06): the buyer is done shopping, so their place in the event's waiting rooms is
/// released at once and the slot goes to the next person. <c>OrderPaid</c> does not say which phase admitted the buyer, so
/// the user leaves every phase of the event; <see cref="IWaitingRoom.LeaveAsync"/> is a no-op for a phase the user is not
/// in, which makes the handler idempotent and tolerant of "not in the queue". A room that cannot be reached throws
/// <see cref="QueueUnavailableException"/> so the outbox retries. This is a separate handler from the inventory commit, so
/// it has its own consumer receipt and a failure here never blocks the seats from being marked sold.
/// </summary>
public sealed class OrderPaidLeaveQueueHandler(IAppDbContext db, IWaitingRoom room) : IIntegrationEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        var phases = await db.Events.AsNoTracking()
            .Where(e => e.Id == message.EventId)
            .SelectMany(e => e.Phases)
            .Select(p => new { p.Id, p.OpensAt, p.ClosesAt, p.QueueEnabled })
            .ToListAsync(ct);

        foreach (var phase in phases)
        {
            await room.LeaveAsync(new SaleWindow(message.EventId, phase.Id, phase.OpensAt, phase.ClosesAt, phase.QueueEnabled), message.UserId, ct);
        }
    }
}
