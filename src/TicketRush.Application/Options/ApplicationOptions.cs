using System.ComponentModel.DataAnnotations;

namespace TicketRush.Application.Options;

/// <summary>Virtual waiting room settings (spec 06).</summary>
public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    [Range(1, 10_000)]
    public int AdmissionsPerSecond { get; set; } = 50;

    [Range(1, 60)]
    public int AdmissionTokenTtlMinutes { get; set; } = 10;
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
}
