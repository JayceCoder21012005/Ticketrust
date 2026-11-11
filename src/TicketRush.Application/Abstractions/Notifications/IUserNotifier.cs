namespace TicketRush.Application.Abstractions.Notifications;

/// <summary>One row of the in-app inbox as the client sees it (the list API and the realtime push share this shape).</summary>
public sealed record NotificationItem(Guid Id, string Type, string Title, string Body, string? Link, bool Read, DateTimeOffset CreatedAt);

/// <summary>
/// Best-effort realtime push to one user's open tabs. Failing to push must never fail the caller: the inbox and the polling
/// fallback show the same data on the next fetch.
/// </summary>
public interface IUserNotifier
{
    Task PushAsync(Guid userId, NotificationItem item, CancellationToken ct);

    Task PushOrderStatusAsync(Guid userId, Guid orderId, string status, CancellationToken ct);
}
