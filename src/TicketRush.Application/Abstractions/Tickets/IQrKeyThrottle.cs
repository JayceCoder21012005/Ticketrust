namespace TicketRush.Application.Abstractions.Tickets;

/// <param name="Allowed">False once the caller used up the window's requests.</param>
/// <param name="RetryAfter">When refused: how long until the window resets. Zero when allowed.</param>
public sealed record ThrottleResult(bool Allowed, TimeSpan RetryAfter);

/// <summary>
/// Limits how often one user may fetch a ticket's QR key (spec 10: 10 requests per minute). A request that ends in 404 or 409
/// still costs a slot, so the endpoint cannot be used to probe ticket ids. Spec 12 replaces this with its named
/// sliding-window policy.
/// </summary>
public interface IQrKeyThrottle
{
    /// <exception cref="ThrottleUnavailableException">The counter store could not be reached: callers fail closed.</exception>
    Task<ThrottleResult> TryAcquireAsync(Guid userId, CancellationToken ct);
}

/// <summary>The throttle's store (Redis) did not answer. The request is refused rather than served unthrottled.</summary>
public sealed class ThrottleUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
