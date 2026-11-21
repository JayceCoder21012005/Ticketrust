using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Tickets.GetTicket;

/// <summary>
/// <c>GET /tickets/{id}</c>: one of the caller's tickets with its event and the caller's other tickets of the same order.
/// Another user's ticket and an unknown id are both 404, so ids cannot be probed.
/// </summary>
public sealed class GetTicketHandler(IAppDbContext db, IUserDirectory users, TimeProvider time)
{
    public async Task<Result<TicketDetail>> HandleAsync(Guid userId, Guid ticketId, CancellationToken ct)
    {
        var row = await TicketQueries.Owned(db, userId).Where(r => r.Id == ticketId).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return TicketErrors.NotFound;
        }

        var item = (await new TicketViews(db, users).ToItemsAsync([row], userId, ct))[0];
        var siblings = await TicketQueries.Owned(db, userId)
            .Where(r => r.OrderId == row.OrderId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);
        return new TicketDetail(
            item.Id, item.OrderId, item.TierName, item.Seat, item.Status, item.HolderName, row.ToSummary(), siblings, await ResaleOfAsync(row, ct));
    }

    private async Task<TicketResale> ResaleOfAsync(TicketRow row, CancellationToken ct)
    {
        var face = await db.Events.AsNoTracking().Where(e => e.Id == row.EventId).SelectMany(e => e.Tiers)
            .Where(t => t.Id == row.TierId).Select(t => t.Price).FirstAsync(ct);
        var canChange = row.Status == TicketStatus.Valid && row.EventStatus == EventStatus.Published && ResaleRules.IsOpen(row.StartsAt, time.GetUtcNow());
        if (row.Status != TicketStatus.Listed)
        {
            return new TicketResale(face.Amount, face.Currency, canChange, null, null);
        }

        var listing = await db.ResaleListings.AsNoTracking()
            .Where(l => l.TicketId == row.Id && (l.Status == ResaleListingStatus.Active || l.Status == ResaleListingStatus.Reserved))
            .Select(l => new TicketListingState(l.Id, l.Price.Amount, l.Status, l.WaitlistUntil))
            .FirstOrDefaultAsync(ct);
        var transfer = await db.TicketTransfers.AsNoTracking()
            .Where(t => t.TicketId == row.Id && t.Status == TicketTransferStatus.Pending)
            .Select(t => new TicketTransferState(t.Id, t.ToEmail, t.ExpiresAt))
            .FirstOrDefaultAsync(ct);
        return new TicketResale(face.Amount, face.Currency, canChange, listing, transfer);
    }
}
