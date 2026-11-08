using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Orders;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// <c>OrderPaid</c> consumer (spec 08 / 06): the buyer is done shopping, so their place in the event's waiting rooms is
/// released at once and the slot goes to the next person. <c>OrderPaid</c> does not say which phase admitted the buyer, so
/// the user leaves every phase that had opened by <see cref="OrderPaid.PaidAt"/> (the buyer can only have been admitted to
/// one of those). A later phase's pre-queue registration is left alone: <c>queue_leave.lua</c> would otherwise drop the
/// buyer's shuffle place there. <see cref="IWaitingRoom.LeaveAsync"/> is a no-op for a phase the user is not in, which makes
/// the handler idempotent and tolerant of "not in the queue". A room that cannot be reached throws
/// <see cref="QueueUnavailableException"/> so the outbox retries.
/// <para>
/// Isolation: this is its own handler with its own consumer receipt, registered after the inventory commit. The outbox runs a
/// message's handlers in registration order and stops at the first failure, so a failing leave never delays the commit, while
/// a failing commit skips the leave for that attempt (it runs on a later retry). If the commit is dead-lettered the leave never
/// runs and the buyer's admission simply expires with its own TTL.
/// </para>
/// </summary>
public sealed class OrderPaidLeaveQueueHandler(IAppDbContext db, IWaitingRoom room) : IIntegrationEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        var phases = await db.Events.AsNoTracking()
            .Where(e => e.Id == message.EventId)
            .SelectMany(e => e.Phases)
            .Where(p => p.OpensAt <= message.PaidAt)
            .Select(p => new { p.Id, p.OpensAt, p.ClosesAt, p.QueueEnabled })
            .ToListAsync(ct);

        foreach (var phase in phases)
        {
            await room.LeaveAsync(new SaleWindow(message.EventId, phase.Id, phase.OpensAt, phase.ClosesAt, phase.QueueEnabled), message.UserId, ct);
        }
    }
}
