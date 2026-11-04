namespace TicketRush.Application.Abstractions.Queue;

/// <summary>Tells the queue workers which sale phases need attention right now (spec 06).</summary>
public interface IQueuePhaseSource
{
    /// <summary>
    /// Queued phases of <b>published</b> events that are open, close within the last minute (so the worker can mark them
    /// Closed), or open within <paramref name="lookahead"/>.
    /// </summary>
    Task<IReadOnlyList<SaleWindow>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct);
}
