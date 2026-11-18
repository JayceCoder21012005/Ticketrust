using Microsoft.Extensions.Caching.Hybrid;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Catalog;

/// <summary>
/// HybridCache decorator (spec 04, C6). HybridCache runs one factory per key at a time, so a burst of requests for
/// an uncached event costs a single database query (no stampede). Entries are tagged so event writes (spec 05)
/// can evict them through <see cref="ICatalogCacheInvalidator"/>.
/// </summary>
/// Availability of a published event is read live from the Redis inventory (spec 07) and never cached here.
internal sealed class CachedEventCatalogReader(
    EventCatalogReader inner,
    HybridCache cache,
    IHoldCatalog holdCatalog,
    IInventoryStore inventory,
    IInventorySeeder seeder)
    : IEventCatalogReader, ICatalogCacheInvalidator
{
    public const string ListTag = "events:list";

    private static readonly HybridCacheEntryOptions Detail = Lifetime(TimeSpan.FromMinutes(10));
    private static readonly HybridCacheEntryOptions List = Lifetime(TimeSpan.FromSeconds(30));
    private static readonly HybridCacheEntryOptions Availability = Lifetime(TimeSpan.FromSeconds(1));
    private static readonly HybridCacheEntryOptions Cities = Lifetime(TimeSpan.FromMinutes(10));

    public static string EventTag(Guid eventId) => $"event:{eventId}";

    /// <summary>Detail is looked up by slug, before its id is known, so it is tagged by slug too.</summary>
    public static string SlugTag(string slug) => $"event-slug:{slug}";

    public async Task<EventPage> ListAsync(ListEventsQuery query, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"catalog:list:{query.Q}|{query.City}|{query.Category}|{query.From:O}|{query.To:O}|{query.PriceMax}|{query.Cursor}|{query.Limit}",
            (inner, query),
            static (state, token) => new ValueTask<EventPage>(state.inner.ListAsync(state.query, token)),
            List,
            [ListTag],
            ct);

    public async Task<EventDetail?> GetBySlugAsync(string slug, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"catalog:event:{slug}",
            (inner, slug),
            static (state, token) => new ValueTask<EventDetail?>(state.inner.GetBySlugAsync(state.slug, token)),
            Detail,
            [SlugTag(slug)],
            ct);

    public async Task<SeatMap?> GetSeatMapAsync(Guid eventId, CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            $"catalog:seat-map:{eventId}",
            (inner, eventId),
            static (state, token) => new ValueTask<SeatMap?>(state.inner.GetSeatMapAsync(state.eventId, token)),
            Detail,
            [EventTag(eventId)],
            ct);

    public async Task<EventAvailability?> GetAvailabilityAsync(Guid eventId, CancellationToken ct)
    {
        if (await holdCatalog.GetAsync(eventId, ct) is not { } entry)
        {
            // Ended events have no live inventory; their (static) answer can be cached briefly.
            return await cache.GetOrCreateAsync(
                $"catalog:availability:{eventId}",
                (inner, eventId),
                static (state, token) => new ValueTask<EventAvailability?>(state.inner.GetAvailabilityAsync(state.eventId, token)),
                Availability,
                [EventTag(eventId)],
                ct);
        }

        var snapshot = await inventory.GetSnapshotAsync(eventId, entry.GaTierIds, ct);
        if (!snapshot.Warm)
        {
            await seeder.SeedAsync(eventId, ct);
            snapshot = await inventory.GetSnapshotAsync(eventId, entry.GaTierIds, ct);
        }

        return ToAvailability(entry, snapshot);
    }

    private static EventAvailability ToAvailability(HoldCatalogEntry entry, InventorySnapshot snapshot)
    {
        var unavailable = snapshot.Held.Union(snapshot.Sold).Order().ToList();
        var taken = unavailable.ToHashSet();
        var tiers = entry.Tiers.Select(t => new TierRemaining(
                t.Id,
                t.Kind == TicketKind.GeneralAdmission
                    ? snapshot.GaRemaining.GetValueOrDefault(t.Id)
                    : entry.Seats.Count(s => s.TierId == t.Id && !taken.Contains(s.Index))))
            .ToList();
        return new EventAvailability(snapshot.Version, unavailable, tiers);
    }

    public async Task<IReadOnlyList<string>> ListCitiesAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            "catalog:cities",
            inner,
            static (reader, token) => new ValueTask<IReadOnlyList<string>>(reader.ListCitiesAsync(token)),
            Cities,
            [ListTag],
            ct);

    public async Task<HomeSections> GetHomeAsync(CancellationToken ct) =>
        await cache.GetOrCreateAsync(
            "catalog:home",
            inner,
            static (reader, token) => new ValueTask<HomeSections>(reader.GetHomeAsync(token)),
            List,
            [ListTag],
            ct);

    public async Task InvalidateEventAsync(Guid eventId, string slug, CancellationToken ct) =>
        await cache.RemoveByTagAsync([EventTag(eventId), SlugTag(slug), ListTag], ct);

    private static HybridCacheEntryOptions Lifetime(TimeSpan expiration) =>
        new() { Expiration = expiration, LocalCacheExpiration = expiration };
}
