using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Orders;

/// <summary>
/// Builds <see cref="OrderResponse"/>. Prices are the order's own (copied from the hold); only display labels (seat
/// row/number, tier name) are read from the event, which may be cancelled by now, so no published-only catalog is used.
/// </summary>
internal sealed class OrderViews(IAppDbContext db, TimeProvider time)
{
    public async Task<OrderResponse> ToResponseAsync(Order order, CancellationToken ct)
    {
        var seatLines = order.Items.Where(i => i.SeatIndex is not null).OrderBy(i => i.SeatIndex).ToList();
        var gaLines = order.Items.Where(i => i.SeatIndex is null).ToList();
        var seatLabels = await SeatLabels.LoadAsync(db, [order.EventId], seatLines.Select(i => i.SeatIndex!.Value).ToArray(), ct);
        var tierNames = await LoadTierNamesAsync(order.EventId, gaLines, ct);
        var ticketCount = order.Status == OrderStatus.Paid
            ? await db.Tickets.AsNoTracking().CountAsync(t => t.OrderId == order.Id && t.Status != TicketStatus.Voided, ct)
            : (int?)null;

        var seats = seatLines.Select(i => ToSeat(order.EventId, i, seatLabels)).ToList();
        var ga = gaLines
            .Select(i => new OrderGaLine(i.TierId, tierNames.GetValueOrDefault(i.TierId, string.Empty), i.Quantity, i.UnitPrice.Amount))
            .ToList();

        return new OrderResponse(
            order.Id,
            order.EventId,
            order.Status,
            seats,
            ga,
            order.Total.Amount,
            new OrderBuyer(order.Buyer.Name, order.Buyer.Email, order.Buyer.Phone),
            order.ExpiresAt,
            time.GetUtcNow(),
            order.CreatedAt,
            order.PaidAt,
            order.RefundReason,
            ticketCount);
    }

    private static OrderSeat ToSeat(Guid eventId, OrderItem item, Dictionary<(Guid EventId, int Index), SeatLabel> labels)
    {
        var label = labels.GetValueOrDefault((eventId, item.SeatIndex!.Value)) ?? new SeatLabel(string.Empty, string.Empty, string.Empty);
        return new OrderSeat(item.SeatIndex.Value, label.Section, label.Row, label.Number, item.TierId, item.UnitPrice.Amount);
    }

    private async Task<Dictionary<Guid, string>> LoadTierNamesAsync(Guid eventId, List<OrderItem> gaLines, CancellationToken ct)
    {
        if (gaLines.Count == 0)
        {
            return [];
        }

        var tierIds = gaLines.Select(i => i.TierId).Distinct().ToList();
        var rows = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId)
            .SelectMany(e => e.Tiers)
            .Where(t => tierIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);
        return rows.ToDictionary(t => t.Id, t => t.Name);
    }
}
