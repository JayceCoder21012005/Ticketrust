using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Tickets.GetTicket;

/// <summary>
/// <c>GET /tickets/{id}</c>: one of the caller's tickets with its event and the caller's other tickets of the same order.
/// Another user's ticket and an unknown id are both 404, so ids cannot be probed.
/// </summary>
public sealed class GetTicketHandler(IAppDbContext db, IUserDirectory users)
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
        return new TicketDetail(item.Id, item.OrderId, item.TierName, item.Seat, item.Status, item.HolderName, row.ToSummary(), siblings);
    }
}
