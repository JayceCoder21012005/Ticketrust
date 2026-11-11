using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Follows.UnfollowEvent;

/// <summary>
/// <c>DELETE /events/{id}/follow</c>: idempotent, and allowed whatever the event's state (a follower of an event that has since
/// ended or an id that never existed has nothing left to undo).
/// </summary>
public sealed class UnfollowEventHandler(IAppDbContext db)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        await db.EventFollows.Where(f => f.UserId == userId && f.EventId == eventId).ExecuteDeleteAsync(ct);
        return Result.Success();
    }
}
