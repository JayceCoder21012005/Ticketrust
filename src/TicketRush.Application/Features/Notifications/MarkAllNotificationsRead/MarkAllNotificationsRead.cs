using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications.MarkAllNotificationsRead;

/// <summary><c>POST /me/notifications/read-all</c>: one set-based UPDATE on this user's unread rows (their earlier read times stay).</summary>
public sealed class MarkAllNotificationsReadHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(n => n.ReadAt, now), ct);
        return Result.Success();
    }
}
