namespace TicketRush.Application.Abstractions.Queue;

/// <summary>A sale phase as the waiting room sees it. <see cref="AccessCodeHash"/> never leaves the server.</summary>
public sealed record SalePhaseInfo(SaleWindow Window, string? AccessCodeHash);

public sealed record EventSales(Guid EventId, IReadOnlyList<SalePhaseInfo> Phases);

/// <summary>
/// Sale phases of a published event, served from a short in-process cache so the queue endpoints, which are polled by
/// the whole crowd, stay off Postgres after the first call (spec 06, p99 &lt; 20 ms).
/// </summary>
public interface ISaleWindowProvider
{
    /// <returns><c>null</c> when the event does not exist or is not published (drafts and cancelled events stay hidden).</returns>
    Task<EventSales?> GetAsync(Guid eventId, CancellationToken ct);
}
