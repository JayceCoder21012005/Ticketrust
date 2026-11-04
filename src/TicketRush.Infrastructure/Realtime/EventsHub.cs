using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TicketRush.Application.Features.Catalog;

namespace TicketRush.Infrastructure.Realtime;

/// <summary>
/// Live seat map of one event (spec 07). Lives in Infrastructure, not Api: the Redis backplane names its channels after
/// the hub's full type name, so the Worker's broadcaster must send through this very type.
/// </summary>
[Authorize]
public sealed class EventsHub : Hub
{
    public const string Path = "/hubs/events";
    public const string SeatDeltaMethod = "SeatDelta";

    public static string Group(Guid eventId) => $"event:{eventId:N}";

    public Task JoinEvent(Guid eventId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(eventId));

    public Task LeaveEvent(Guid eventId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(eventId));
}

/// <summary>
/// Seat changes of one event since the client's last delta. <paramref name="From"/>..<paramref name="V"/> is the range of
/// inventory versions folded into it: a client at version <c>x</c> that receives <c>From &gt; x + 1</c> missed some and
/// refetches <c>/availability</c>.
/// </summary>
public sealed record SeatDelta(long From, long V, int[] Held, int[] Freed, int[] Sold, TierRemaining[] Tiers);
