namespace TicketRush.Application.Abstractions.Messaging;

/// <summary>
/// Exponential backoff with jitter for outbox retries (C11): 1 s, 2 s, 4 s … capped at 10 min.
/// Jitter spreads retries of many failing messages so they don't hit a recovering dependency at once.
/// </summary>
public static class OutboxRetryPolicy
{
    public const int MaxAttempts = 10;

    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(10);

    public static bool IsExhausted(int attempts) => attempts >= MaxAttempts;

    /// <param name="attempts">Number of failed attempts so far (≥ 1).</param>
    /// <param name="random">Value in [0, 1) used for "equal jitter": delay is in [d/2, d).</param>
    public static TimeSpan NextDelay(int attempts, double random)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var exponent = Math.Min(attempts - 1, 30);
        var ceiling = Math.Min(BaseDelay.TotalMilliseconds * Math.Pow(2, exponent), MaxDelay.TotalMilliseconds);
        var delay = (ceiling / 2) + (ceiling / 2 * Math.Clamp(random, 0, 1));
        return TimeSpan.FromMilliseconds(Math.Min(delay, MaxDelay.TotalMilliseconds));
    }
}
