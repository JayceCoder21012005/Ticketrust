using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications.MarkNotificationRead;

/// <summary>
/// <c>POST /me/notifications/{id}/read</c>: idempotent (the first read time is kept). Someone else's notification and an unknown
/// id are both 404, so ids cannot be probed.
/// </summary>
public sealed class MarkNotificationReadHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result> HandleAsync(Guid userId, Guid notificationId, CancellationToken ct)
    {
        var notice = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId, ct);
        if (notice is null)
        {
            return NotificationErrors.NotFound;
        }

        notice.MarkRead(time.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
