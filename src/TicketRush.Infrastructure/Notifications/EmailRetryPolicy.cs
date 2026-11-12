using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>
/// The email delivery worker's backoff (C11), the same shape as the outbox's (exponential, equal jitter) but much longer: spec 09
/// says an SMTP outage only delays email, and outages last hours, not minutes. With the defaults (30 s doubling, 1 h cap, 30
/// attempts) the 29 waits add up to between about 11.5 and 23 hours, so a delivery outlives a long outage before it is parked.
/// </summary>
public sealed class EmailRetryPolicy(TimeSpan baseDelay, TimeSpan maxDelay, int maxAttempts)
{
    public TimeSpan BaseDelay { get; } = baseDelay;

    public TimeSpan MaxDelay { get; } = maxDelay;

    public int MaxAttempts { get; } = maxAttempts;

    public static EmailRetryPolicy From(EmailDeliveryOptions options) => new(options.BaseDelay, options.MaxDelay, options.MaxAttempts);

    public bool IsExhausted(int attempts) => attempts >= MaxAttempts;

    /// <param name="attempts">Failed sends so far (≥ 1).</param>
    /// <param name="random">Value in [0, 1] for "equal jitter": the delay is in [d/2, d], so many deliveries failed by the same
    /// outage do not all come back at the same instant.</param>
    public TimeSpan NextDelay(int attempts, double random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var exponent = Math.Min(attempts - 1, 30);
        var ceiling = Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(2, exponent), MaxDelay.TotalMilliseconds);
        var delay = (ceiling / 2) + (ceiling / 2 * Math.Clamp(random, 0, 1));
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxDelay.TotalMilliseconds));
    }
}
