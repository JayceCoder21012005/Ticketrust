using TicketRush.Application.Abstractions.Notifications;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>Pushes nowhere. Registered with <c>TryAdd</c> so handlers run before the realtime hub exists; the hub's notifier is registered ahead of it.</summary>
internal sealed class NullUserNotifier : IUserNotifier
{
    public Task PushAsync(Guid userId, NotificationItem item, CancellationToken ct) => Task.CompletedTask;

    public Task PushOrderStatusAsync(Guid userId, Guid orderId, string status, CancellationToken ct) => Task.CompletedTask;
}
