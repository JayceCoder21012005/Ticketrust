using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Realtime;

/// <summary>
/// Pushes to a user's open tabs through the SignalR group (and, across processes, the Redis backplane), so the Worker's handlers
/// and the Api's reach the same clients. Best effort by contract: a failing push (Redis down) is logged by exception type only,
/// never the user or the text, and never fails the caller; the inbox and the polling fallback show the same data.
/// <para>
/// Inside an outbox batch (the notification handlers) the send is not made at once but queued on the batch's
/// <see cref="OutboxPostCommit"/>: it leaves only after the batch has committed, so a tab never sees a notice that is not saved
/// yet (or never will be), and a slow backplane never holds the batch transaction open. Elsewhere it is sent at once.
/// </para>
/// </summary>
internal sealed partial class UserNotifier(IHubContext<UserHub> hub, ILogger<UserNotifier> logger) : IUserNotifier
{
    public Task PushAsync(Guid userId, NotificationItem item, CancellationToken ct) =>
        SendNowOrAfterCommit(token => SendAsync(userId, UserHub.NotificationMethod, item, token), ct);

    public Task PushOrderStatusAsync(Guid userId, Guid orderId, string status, CancellationToken ct) =>
        SendNowOrAfterCommit(token => SendAsync(userId, UserHub.OrderStatusChangedMethod, orderId, status, token), ct);

    private static Task SendNowOrAfterCommit(Func<CancellationToken, Task> send, CancellationToken ct) =>
        OutboxPostCommit.TryDefer(send) ? Task.CompletedTask : send(ct);

    private Task SendAsync(Guid userId, string method, object arg1, CancellationToken ct) =>
        TrySendAsync(method, () => hub.Clients.Group(UserHub.Group(userId)).SendAsync(method, arg1, ct), ct);

    private Task SendAsync(Guid userId, string method, object arg1, object arg2, CancellationToken ct) =>
        TrySendAsync(method, () => hub.Clients.Group(UserHub.Group(userId)).SendAsync(method, arg1, arg2, ct), ct);

    private async Task TrySendAsync(string method, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await send();
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogSendFailed(method, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Live push of {Method} failed ({ExceptionType}); the inbox shows it on the next fetch")]
    private partial void LogSendFailed(string method, string exceptionType);
}
