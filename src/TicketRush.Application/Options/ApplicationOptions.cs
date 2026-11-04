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

    [Range(1, 60)]
    public int HoldTtlMinutes { get; set; } = 10;

    [Range(1, 8)]
    public int MaxSeatsPerHold { get; set; } = 8;
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
