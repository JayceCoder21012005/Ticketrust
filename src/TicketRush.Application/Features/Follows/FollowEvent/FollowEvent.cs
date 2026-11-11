using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Follows.FollowEvent;

/// <summary>
/// <c>POST /events/{id}/follow</c>: idempotent. The key (user, event) is the guard: when two requests race, the loser's insert
/// fails on the key, and since the row is then there the answer is the same success.
/// </summary>
public sealed class FollowEventHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        var status = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => (EventStatus?)e.Status).FirstOrDefaultAsync(ct);
        if (status is null)
        {
            return EventErrors.NotFound;
        }

        if (status != EventStatus.Published)
        {
            return EventErrors.NotFollowable;
        }

        if (await IsFollowingAsync(userId, eventId, ct))
        {
            return Result.Success();
        }

        db.EventFollows.Add(EventFollow.Create(userId, eventId, time.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await IsFollowingAsync(userId, eventId, ct))
            {
                // Not the key: something else went wrong with the insert.
                throw;
            }
        }

        return Result.Success();
    }

    private Task<bool> IsFollowingAsync(Guid userId, Guid eventId, CancellationToken ct) =>
        db.EventFollows.AsNoTracking().AnyAsync(f => f.UserId == userId && f.EventId == eventId, ct);
}
