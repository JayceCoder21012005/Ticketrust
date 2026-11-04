using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Catalog;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// Seats, tiers and phases of a published event behind HybridCache. All three are frozen at publish (spec 05), so a long
/// lifetime is safe; the entry carries the catalog's event tag, so a cancellation evicts it with the rest of the catalog.
/// </summary>
public sealed class HoldCatalog(IAppDbContext db, HybridCache cache) : IHoldCatalog
{
    private static readonly HybridCacheEntryOptions Lifetime =
        new() { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(10) };

    /// <summary>HybridCache needs a non-null payload; the wrapper caches "not found" too.</summary>
    private sealed record Cached(HoldCatalogEntry? Entry);

    public async Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(
            $"holds:catalog:{eventId:N}",
            (db, eventId),
            static async (state, token) => new Cached(await LoadAsync(state.db, state.eventId, token)),
            Lifetime,
            [CachedEventCatalogReader.EventTag(eventId)],
            ct)).Entry;

    private static async Task<HoldCatalogEntry?> LoadAsync(IAppDbContext db, Guid eventId, CancellationToken ct)
    {
        var evt = await db.Events
            .AsNoTracking()
            .Where(e => e.Id == eventId && e.Status == EventStatus.Published)
            .Select(e => new
            {
                e.Id,
                e.Slug,
                Tiers = e.Tiers.Select(t => new CatalogTier(t.Id, t.Name, t.Price.Amount, t.Kind, t.Capacity)).ToList(),
                Phases = e.Phases.Select(p => new CatalogPhase(p.Id, p.OpensAt, p.ClosesAt, p.MaxTicketsPerUser)).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (evt is null)
        {
            return null;
        }

        var seats = await db.EventSeats
            .AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Index)
            .Select(s => new CatalogSeat(s.Index, s.Section, s.Row, s.Number, s.X, s.Y, s.TierId))
            .ToListAsync(ct);

        return new HoldCatalogEntry(evt.Id, evt.Slug, seats, evt.Tiers, [.. evt.Phases.OrderBy(p => p.OpensAt)]);
    }
}
