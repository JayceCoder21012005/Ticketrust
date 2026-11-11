using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Infrastructure.Realtime;

/// <summary>
/// One user's live channel (spec 09): new notifications and order status changes reach every open tab. Lives in Infrastructure
/// for the same reason as <see cref="EventsHub"/>: the Redis backplane names its channels after the hub's full type name, so the
/// Worker must send through this very type. The server only pushes: there is no client-callable method, and in particular no way
/// to join a group, because the group is derived from the authenticated <c>sub</c> claim and never chosen by the client.
/// </summary>
[Authorize]
public sealed class UserHub : Hub
{
    public const string Path = "/hubs/user";
    public const string NotificationMethod = "Notification";
    public const string OrderStatusChangedMethod = "OrderStatusChanged";

    public static string Group(Guid userId) => $"user:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var subject = Context.User?.FindFirst(AuthClaims.Subject)?.Value;
        if (!Guid.TryParse(subject, out var userId))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(userId));
        await base.OnConnectedAsync();
    }
}
