namespace TicketRush.Application.Abstractions.Security;

/// <param name="Allowed">The request fits in the window and was counted.</param>
/// <param name="RetryAfter">When refused: how long until the oldest request leaves the window.</param>
public readonly record struct WindowResult(bool Allowed, TimeSpan RetryAfter);

/// <summary>Distributed sliding-window counter (spec 12): the limit holds across every api instance.</summary>
public interface ISlidingWindowStore
{
    /// <summary>Counts one request of <paramref name="partition"/> under <paramref name="policy"/>. Throws when the store cannot answer.</summary>
    Task<WindowResult> HitAsync(string policy, string partition, int limit, TimeSpan window, CancellationToken ct);
}
