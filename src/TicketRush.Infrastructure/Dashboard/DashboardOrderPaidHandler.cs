using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Dashboard;
using TicketRush.Domain.Orders;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Dashboard;

/// <summary>
/// <c>OrderPaid</c> consumer of the dashboard (spec 15). In the outbox's transaction it upserts <c>sales_per_minute</c>
/// (<c>ON CONFLICT … DO UPDATE</c>), then counts the order in <c>dash:{evt}</c> once (a marker per order). Redis being down throws,
/// so the transaction (upsert included) rolls back and the outbox retries; a redelivery after the commit is stopped by the receipt,
/// one between the script and the commit by the marker. Resale orders sell no seat and earn the organizer nothing: skipped.
/// </summary>
public sealed class DashboardOrderPaidHandler(AppDbContext db, IDashboardStore store) : IIntegrationEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        if (message.Kind == OrderKind.Resale)
        {
            return;
        }

        var prices = await db.Events.AsNoTracking().Where(e => e.Id == message.EventId).SelectMany(e => e.Tiers)
            .ToDictionaryAsync(t => t.Id, t => t.Price.Amount, ct);
        var lines = message.Lines
            .GroupBy(l => l.TierId)
            .Select(g => new PaidTierLine(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.Quantity * prices.GetValueOrDefault(g.Key))))
            .ToList();
        var minute = SalesMinute.MinuteOf(message.PaidAt);
        foreach (var line in lines)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO sales_per_minute (event_id, minute, tier_id, tickets, revenue)
                VALUES ({message.EventId}, {minute}, {line.TierId}, {line.Tickets}, {line.Revenue})
                ON CONFLICT (event_id, minute, tier_id)
                DO UPDATE SET tickets = sales_per_minute.tickets + EXCLUDED.tickets, revenue = sales_per_minute.revenue + EXCLUDED.revenue
                """,
                ct);
        }

        var buyer = await db.Orders.AsNoTracking().Where(o => o.Id == message.OrderId).Select(o => o.Buyer.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        await store.RecordPaidAsync(message.EventId, new RecentOrder(message.OrderId, buyer, message.TicketCount, message.TotalAmount, message.PaidAt), lines, ct);
    }
}
