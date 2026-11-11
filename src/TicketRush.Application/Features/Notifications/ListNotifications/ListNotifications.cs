using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Orders;

namespace TicketRush.Application.Features.Notifications.ListNotifications;

/// <param name="Limit">Anything over <see cref="MaxLimit"/> is served as <see cref="MaxLimit"/>; below 1 as 1.</param>
public sealed record ListNotificationsQuery(Guid UserId, string? Cursor, int Limit = ListNotificationsQuery.DefaultLimit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public int EffectiveLimit => Math.Clamp(Limit, 1, MaxLimit);
}

/// <param name="UnreadCount">All of the user's unread notifications, not only the ones on this page.</param>
public sealed record NotificationPage(IReadOnlyList<NotificationItem> Items, string? NextCursor, int UnreadCount);

public sealed class ListNotificationsValidator : AbstractValidator<ListNotificationsQuery>
{
    public ListNotificationsValidator()
    {
        RuleFor(q => q.Cursor)
            .Must(cursor => string.IsNullOrWhiteSpace(cursor) || OrderCursor.TryDecode(cursor.Trim(), out _))
            .WithMessage("Invalid cursor.");
    }
}

/// <summary>
/// <c>GET /me/notifications</c>: newest first, keyset-paged on (created_at, id) like the orders list (same cursor), so notices
/// created in the same instant never repeat or vanish between pages.
/// </summary>
public sealed class ListNotificationsHandler(IAppDbContext db)
{
    public async Task<NotificationPage> HandleAsync(ListNotificationsQuery query, CancellationToken ct)
    {
        var limit = query.EffectiveLimit;
        var notices = db.Notifications.AsNoTracking().Where(n => n.UserId == query.UserId);
        if (!string.IsNullOrWhiteSpace(query.Cursor) && OrderCursor.TryDecode(query.Cursor.Trim(), out var after))
        {
            notices = notices.Where(n => n.CreatedAt < after.CreatedAt || (n.CreatedAt == after.CreatedAt && n.Id.CompareTo(after.Id) < 0));
        }

        var rows = await notices
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(limit + 1)
            .Select(n => new { n.Id, n.Type, n.Title, n.Body, n.Link, Read = n.ReadAt != null, n.CreatedAt })
            .ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == query.UserId && n.ReadAt == null, ct);

        var page = rows.Take(limit).ToList();
        var items = page.Select(r => new NotificationItem(r.Id, r.Type.ToString(), r.Title, r.Body, r.Link, r.Read, r.CreatedAt)).ToList();
        var next = rows.Count > limit ? OrderCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;
        return new NotificationPage(items, next, unread);
    }
}
