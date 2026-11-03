namespace TicketRush.Application.Features.Catalog;

/// <summary>
/// Read side of the public catalog. Implemented in Infrastructure (Postgres full-text search) and wrapped by a
/// HybridCache decorator, so callers never think about caching. Unpublished events are invisible here.
/// </summary>
public interface IEventCatalogReader
{
    /// <summary>Published events that have not ended, ordered by (StartsAt, Id).</summary>
    Task<EventPage> ListAsync(ListEventsQuery query, CancellationToken ct);

    /// <summary>Published or ended events; null for unknown, draft and cancelled ones.</summary>
    Task<EventDetail?> GetBySlugAsync(string slug, CancellationToken ct);

    Task<SeatMap?> GetSeatMapAsync(Guid eventId, CancellationToken ct);

    Task<EventAvailability?> GetAvailabilityAsync(Guid eventId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListCitiesAsync(CancellationToken ct);
}

/// <summary>Called by event writes (spec 05) so the next read sees the change.</summary>
public interface ICatalogCacheInvalidator
{
    Task InvalidateEventAsync(Guid eventId, string slug, CancellationToken ct);
}
