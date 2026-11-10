using System.ComponentModel.DataAnnotations;

namespace TicketRush.Application.Options;

/// <summary>Virtual waiting room settings (spec 06).</summary>
public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    /// <summary>Most buyers the admitter lets in per second.</summary>
    [Range(1, 10_000)]
    public int AdmissionRatePerSecond { get; set; } = 50;

    /// <summary>Cap on shoppers holding an admission at the same time (the backend's real capacity).</summary>
    [Range(1, 100_000)]
    public int MaxActiveShoppers { get; set; } = 500;

    /// <summary>How long an admitted buyer may shop; also the admission token lifetime.</summary>
    [Range(1, 60)]
    public int ShoppingWindowMinutes { get; set; } = 10;

    /// <summary>Queued sales accept joins this long before they open.</summary>
    [Range(1, 240)]
    public int PreQueueMinutes { get; set; } = 30;
}

/// <summary>Seat hold settings (spec 07). Payment window must stay shorter than the hold TTL (C3).</summary>
public sealed class HoldsOptions
{
    public const string SectionName = "Holds";

    /// <summary>How long a hold lasts (spec 07: 480 s). Waitlist offers (13) pass their own TTL to the scripts.</summary>
    [Range(60, 3600)]
    public int TtlSeconds { get; set; } = 480;

    /// <summary>Hard cap on top of the phase's per-user limit.</summary>
    [Range(1, 8)]
    public int MaxSeatsPerHold { get; set; } = 8;

    public TimeSpan Ttl => TimeSpan.FromSeconds(TtlSeconds);
}

/// <summary>Payment fulfilment (spec 08 webhook handling).</summary>
public sealed class FulfilmentOptions
{
    public const string SectionName = "Fulfilment";

    /// <summary>
    /// <c>SET LOCAL lock_timeout</c> of the fulfilment transaction: a lock held this long (a stuck cancel, a long seat wait)
    /// fails the webhook with a 5xx the provider retries, instead of piling up connections behind it.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.100", "00:01:00")]
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Backstop for cancelled events whose <c>EventCancelled</c> message never settled the orders (spec 08, B9): e.g. it was
/// dead-lettered after a long outage. Leader-elected worker pass.
/// </summary>
public sealed class CancelledEventSettlementOptions
{
    public const string SectionName = "Payments:CancelledEvents";

    /// <summary>Time between passes. Normally the outbox settles a cancellation within seconds; this only catches leftovers.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Cancelled events listed per query (index <c>ix_orders_event_id</c> answers the "orders left?" check).</summary>
    [Range(1, 500)]
    public int EventsPerPage { get; set; } = 20;
}

/// <summary>Order expiry sweep (spec 08).</summary>
public sealed class OrderExpiryOptions
{
    public const string SectionName = "Payments:Expiry";

    /// <summary>
    /// How long past its own expiry a payment session of an order that is no longer open may stay Pending before the sweep asks
    /// the gateway about it again (money that reached it late, a second session, a lost webhook). Long enough for the
    /// provider's own webhook to arrive first in the normal case.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan AttemptGrace { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Refund worker (spec 08): how a refund the gateway DECLINES is retried before a person has to look at it. A gateway
/// outage is not a decline: it is simply tried again on the next pass and never parks anything.
/// </summary>
public sealed class RefundOptions
{
    public const string SectionName = "Refunds";

    /// <summary>Declines after which the refund is parked (<c>refund_needs_attention</c>) instead of retried.</summary>
    [Range(1, 100)]
    public int MaxDeclines { get; set; } = 8;

    /// <summary>Wait after the first decline; doubled after each further one.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait between two tries.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Wait after the <paramref name="declines"/>-th decline: <see cref="RetryBaseDelay"/> × 2^(declines − 1), at most <see cref="RetryMaxDelay"/>.</summary>
    public TimeSpan DelayAfter(int declines)
    {
        var doublings = Math.Clamp(declines - 1, 0, 40);
        var ticks = RetryBaseDelay.Ticks * Math.Pow(2, doublings);
        return ticks >= RetryMaxDelay.Ticks ? RetryMaxDelay : TimeSpan.FromTicks((long)ticks);
    }
}

/// <summary>Rate limit budgets (spec 12).</summary>
public sealed class RateLimitsOptions
{
    public const string SectionName = "RateLimits";

    [Range(1, 100_000)]
    public int GlobalPerIpPerMinute { get; set; } = 600;

    [Range(1, 1_000)]
    public int LoginPerIpPerMinute { get; set; } = 10;
}

public sealed class FrontendOptions
{
    public const string SectionName = "Frontend";

    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The web app's <c>POST /api/revalidate</c> as the worker reaches it (e.g. <c>http://web:3000/api/revalidate</c>).
    /// Blank disables web cache refresh; pages then update when their ISR window ends.
    /// </summary>
    public string? RevalidateUrl { get; set; }

    /// <summary>Shared with the web app's <c>REVALIDATE_SECRET</c>. Secret: AppHost parameter / env only.</summary>
    public string? RevalidateSecret { get; set; }
}
