using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;

namespace TicketRush.Application.Features.Follows.ListMyFollows;

/// <summary>
/// <c>GET /me/follows</c> (UI refresh): the caller's followed events that are still published and not over, soonest first, as
/// catalog cards. Feeds the account overview and "for you".
/// </summary>
public sealed class ListMyFollowsHandler(IAppDbContext db, TimeProvider time)
{
    public const int Limit = 20;

    public async Task<IReadOnlyList<EventSummary>> HandleAsync(Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        return await (
                from f in db.EventFollows.AsNoTracking()
                where f.UserId == userId
                join e in db.Events on f.EventId equals e.Id
                where e.Status == EventStatus.Published && e.EndsAt > now
                join v in db.Venues on e.VenueId equals v.Id
                orderby e.StartsAt, e.Id
                select new EventSummary(
                    e.Id,
                    e.Slug,
                    e.Title,
                    e.Category,
                    e.StartsAt,
                    v.Name,
                    e.City,
                    e.CoverImageUrl,
                    e.Tiers.Min(t => (long?)t.Price.Amount),
                    e.Phases.Where(p => p.OpensAt > now).Min(p => (DateTimeOffset?)p.OpensAt),
                    e.Phases.Any(p => p.OpensAt <= now && p.ClosesAt > now),
                    e.IsSoldOut))
            .Take(Limit)
            .ToListAsync(ct);
    }
}
