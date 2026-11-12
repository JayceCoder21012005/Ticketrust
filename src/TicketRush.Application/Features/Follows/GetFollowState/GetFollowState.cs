using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;

namespace TicketRush.Application.Features.Follows.GetFollowState;

public sealed record FollowState(bool Following);

/// <summary>
/// <c>GET /events/{id}/follow</c>: whether the caller follows the event. Only the caller's own row is read, so an unknown or
/// someone else's id simply answers <c>false</c>.
/// </summary>
public sealed class GetFollowStateHandler(IAppDbContext db)
{
    public async Task<FollowState> HandleAsync(Guid userId, Guid eventId, CancellationToken ct) =>
        new(await db.EventFollows.AsNoTracking().AnyAsync(f => f.UserId == userId && f.EventId == eventId, ct));
}
