using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// Sale phases of published events behind a 30 s in-process cache (single-flight per key, like the catalog). Phases are
/// frozen once an event is published, so staleness only matters for a cancellation, which clients see within seconds.
/// </summary>
public sealed class SaleWindowProvider(IAppDbContext db, HybridCache cache) : ISaleWindowProvider
{
    private static readonly HybridCacheEntryOptions Lifetime =
        new() { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(30) };

    /// <summary>HybridCache needs a non-null payload type; the wrapper lets "not found" be cached too (no DB probing).</summary>
    private sealed record Cached(EventSales? Sales);

    public async Task<EventSales?> GetAsync(Guid eventId, CancellationToken ct) =>
        (await cache.GetOrCreateAsync(
            $"queue:sales:{eventId:N}",
            (db, eventId),
            static async (state, token) => new Cached(await LoadAsync(state.db, state.eventId, token)),
            Lifetime,
            cancellationToken: ct)).Sales;

    private static async Task<EventSales?> LoadAsync(IAppDbContext db, Guid eventId, CancellationToken ct)
    {
        var found = await db.Events
            .Where(e => e.Id == eventId && e.Status == EventStatus.Published)
            .Select(e => new
            {
                e.Id,
                Phases = e.Phases
                    .Select(p => new { p.Id, p.OpensAt, p.ClosesAt, p.QueueEnabled, p.AccessCodeHash })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);

        return found is null
            ? null
            : new EventSales(
                found.Id,
                [.. found.Phases.Select(p => new SalePhaseInfo(new SaleWindow(found.Id, p.Id, p.OpensAt, p.ClosesAt, p.QueueEnabled), p.AccessCodeHash))]);
    }
}
