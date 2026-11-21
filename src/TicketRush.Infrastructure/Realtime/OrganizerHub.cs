using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Application.Features.Dashboard;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Realtime;

/// <summary>
/// The organizer dashboard's live channel (spec 15). <see cref="JoinDashboard"/> checks in Postgres that the caller owns the event
/// before adding the connection to <c>org:{eventId}</c>; anyone else gets <c>dashboard.not_found</c>, like the REST routes' 404. A
/// join also marks the connection as a viewer, and the client repeats it as a heartbeat; the Worker's broadcaster only works for
/// events with a viewer. Lives in Infrastructure for the backplane, like <see cref="EventsHub"/>.
/// </summary>
[Authorize(Policy = "RequireOrganizer")]
public sealed class OrganizerHub(AppDbContext db, IDashboardStore store, TimeProvider time) : Hub
{
    public const string Path = "/hubs/organizer";
    public const string SnapshotMethod = "DashboardSnapshot";

    private const string WatchingKey = "dashboard:watching";

    public static string Group(Guid eventId) => $"org:{eventId:N}";

    public async Task JoinDashboard(Guid eventId)
    {
        var subject = Context.User?.FindFirst(AuthClaims.Subject)?.Value;
        if (!Guid.TryParse(subject, out var organizerId)
            || !await db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId && e.OrganizerId == organizerId, Context.ConnectionAborted))
        {
            throw new HubException(DashboardErrors.NotFound.Code);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(eventId), Context.ConnectionAborted);
        await store.AddViewerAsync(eventId, Context.ConnectionId, time.GetUtcNow(), Context.ConnectionAborted);
        Watching().Add(eventId);
    }

    public async Task LeaveDashboard(Guid eventId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(eventId));
        await store.RemoveViewerAsync(eventId, Context.ConnectionId, CancellationToken.None);
        Watching().Remove(eventId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var eventId in Watching())
        {
            try
            {
                await store.RemoveViewerAsync(eventId, Context.ConnectionId, CancellationToken.None);
            }
            catch (Application.Abstractions.Inventory.InventoryUnavailableException)
            {
                // The viewer times out on its own (no heartbeat).
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private HashSet<Guid> Watching()
    {
        if (Context.Items[WatchingKey] is not HashSet<Guid> watching)
        {
            watching = [];
            Context.Items[WatchingKey] = watching;
        }

        return watching;
    }
}

/// <summary>Sends snapshots through <see cref="OrganizerHub"/> (from the Worker, via the Redis backplane).</summary>
public sealed class DashboardPublisher(IHubContext<OrganizerHub> hub) : IDashboardPublisher
{
    public Task PublishAsync(DashboardSnapshot snapshot, CancellationToken ct) =>
        hub.Clients.Group(OrganizerHub.Group(snapshot.EventId)).SendAsync(OrganizerHub.SnapshotMethod, snapshot, ct);
}
