using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// What the tickets table records as sold (every status but <see cref="TicketStatus.Voided"/>), so the reconciler can
/// rebuild Redis after a Redis loss. Tickets are the truth: a claimed <c>sold_count</c> without its tickets was rolled back.
/// </summary>
public sealed class DbSoldInventorySource(AppDbContext db) : ISoldInventorySource
{
    public async Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct)
    {
        var sold = db.Tickets.AsNoTracking().Where(t => t.EventId == eventId && t.Status != TicketStatus.Voided);

        var seats = await sold.Where(t => t.SeatIndex != null).Select(t => t.SeatIndex!.Value).OrderBy(index => index).ToListAsync(ct);
        var ga = await sold.Where(t => t.SeatIndex == null)
            .GroupBy(t => t.TierId)
            .Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.TierId, row => row.Count, ct);
        var byUser = await sold
            .GroupBy(t => t.OwnerUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(row => row.UserId, row => row.Count, ct);

        return new SoldInventory(seats, ga, byUser);
    }
}
