using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Gates;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Gate;

/// <summary><c>GET /gate/events</c>: the events assigned to the caller, tonight's first, then the coming ones, then the finished ones.</summary>
public sealed class ListGateEventsHandler(IAppDbContext db, TimeProvider time)
{
    private static readonly TimeSpan Soon = TimeSpan.FromHours(12);

    public async Task<IReadOnlyList<GateEventItem>> HandleAsync(Guid staffUserId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var rows = await (
            from a in db.GateAssignments.AsNoTracking()
            where a.UserId == staffUserId
            join e in db.Events.AsNoTracking() on a.EventId equals e.Id
            join v in db.Venues.AsNoTracking() on e.VenueId equals v.Id
            select new GateEventItem(e.Id, e.Slug, e.Title, e.StartsAt, e.EndsAt, v.Name, e.City, a.GateName))
            .ToListAsync(ct);

        return [.. rows.OrderBy(r => Group(r, now)).ThenBy(r => r.StartsAt)];
    }

    private static int Group(GateEventItem r, DateTimeOffset now) =>
        r.EndsAt <= now ? 2 : r.StartsAt <= now + Soon ? 0 : 1;
}

/// <summary><c>GET /gate/events/{eventId}/stats</c>: the live counter of the gate screen.</summary>
public sealed class GetGateStatsHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result<GateStatsResponse>> HandleAsync(Guid staffUserId, Guid eventId, CancellationToken ct)
    {
        if (await GateAssignments.FindGateNameAsync(db, eventId, staffUserId, ct) is null)
        {
            return GateErrors.NotAssigned;
        }

        var since = time.GetUtcNow().AddMinutes(-1);
        var counts = await db.Tickets.AsNoTracking()
            .Where(t => t.EventId == eventId && t.Status != TicketStatus.Voided)
            .GroupBy(t => 1)
            .Select(g => new { Total = g.Count(), CheckedIn = g.Count(t => t.Status == TicketStatus.CheckedIn) })
            .SingleOrDefaultAsync(ct);
        var lastMinute = await db.CheckIns.AsNoTracking()
            .CountAsync(c => c.EventId == eventId && c.Result == ScanResult.Admitted && c.ScannedAt > since, ct);
        return new GateStatsResponse(counts?.CheckedIn ?? 0, counts?.Total ?? 0, lastMinute);
    }
}
