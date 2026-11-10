using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// <c>OrderPaid</c> consumer (spec 08, acceptance 6): marks the order's seats sold in Redis, counts them as bought and takes
/// the GA tickets (<c>inventory_commit.lua</c>, spec 07). The order id is the idempotency key: a redelivery answers
/// <see cref="CommitStatus.Duplicate"/> and changes nothing, which is a success here. This also covers a late payment
/// whose hold has expired or been replaced; the script then takes the GA from <c>remaining</c> and still sells the seats.
/// <para>
/// Redis being unreachable throws <see cref="InventoryUnavailableException"/> so the outbox retries; the event is durable
/// and the database already holds the tickets, so the reconciler is the backstop if every retry is spent. The commit also
/// runs for an event whose inventory was never warmed: the next warm-up recomputes GA remaining from the database.
/// </para>
/// <para>
/// Registered before <c>OrderPaidLeaveQueueHandler</c>: the outbox stops at the first failing handler of a
/// message, so the critical commit is never held up by the non-critical leave (the reverse is only a delayed leave).
/// </para>
/// </summary>
public sealed partial class OrderPaidInventoryCommitHandler(IAppDbContext db, IInventoryStore store, ILogger<OrderPaidInventoryCommitHandler> logger)
    : IIntegrationEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        // Ascending (C9); the store sorts again, so a caller can never break the lock order.
        var seats = message.Lines.Where(l => l.SeatIndex is not null).Select(l => l.SeatIndex!.Value).Order().ToList();
        var ga = message.Lines
            .Where(l => l.SeatIndex is null && l.Quantity > 0)
            .GroupBy(l => l.TierId)
            .Select(group => new GaLine(group.Key, group.Sum(l => l.Quantity)))
            .ToList();
        var gaTiers = await GaTiersAsync(message.EventId, ct);

        var outcome = await store.CommitAsync(message.EventId, message.OrderId, message.HoldId, message.UserId, seats, ga, gaTiers, ct);
        if (outcome.Status == CommitStatus.Duplicate)
        {
            LogAlreadyCommitted(logger, message.OrderId, message.EventId);
        }
    }

    private async Task<List<Guid>> GaTiersAsync(Guid eventId, CancellationToken ct) =>
        await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .SelectMany(e => e.Tiers)
            .Where(t => t.Kind == TicketKind.GeneralAdmission)
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToListAsync(ct);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Order {OrderId} of event {EventId} was already committed to the inventory")]
    private static partial void LogAlreadyCommitted(ILogger logger, Guid orderId, Guid eventId);
}
