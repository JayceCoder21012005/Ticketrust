using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Catalog;

public sealed class ListEventsHandler(IEventCatalogReader reader)
{
    public Task<EventPage> HandleAsync(ListEventsQuery query, CancellationToken ct) => reader.ListAsync(query.Normalized(), ct);
}

public sealed class GetEventHandler(IEventCatalogReader reader)
{
    public async Task<Result<EventDetail>> HandleAsync(string slug, CancellationToken ct) =>
        await reader.GetBySlugAsync(slug.Trim().ToLowerInvariant(), ct) is { } detail ? detail : EventErrors.NotFound;
}

public sealed class GetSeatMapHandler(IEventCatalogReader reader)
{
    public async Task<Result<SeatMap>> HandleAsync(Guid eventId, CancellationToken ct) =>
        await reader.GetSeatMapAsync(eventId, ct) is { } map ? map : EventErrors.NotFound;
}

/// <summary><c>serverNow</c> is stamped per response (never cached) so clients can compute their clock offset (C10).</summary>
public sealed class GetAvailabilityHandler(IEventCatalogReader reader, TimeProvider time)
{
    public async Task<Result<AvailabilityResponse>> HandleAsync(Guid eventId, CancellationToken ct) =>
        await reader.GetAvailabilityAsync(eventId, ct) is { } availability
            ? new AvailabilityResponse(time.GetUtcNow(), availability.Version, availability.Unavailable, availability.Tiers)
            : EventErrors.NotFound;
}

public sealed class ListCitiesHandler(IEventCatalogReader reader)
{
    public Task<IReadOnlyList<string>> HandleAsync(CancellationToken ct) => reader.ListCitiesAsync(ct);
}
