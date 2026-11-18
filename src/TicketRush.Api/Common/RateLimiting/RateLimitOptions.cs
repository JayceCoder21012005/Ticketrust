namespace TicketRush.Api.Common.RateLimiting;

/// <summary>One policy: at most <see cref="Permit"/> requests per <see cref="Window"/> and partition.</summary>
public sealed class RateLimitRule
{
    public RateLimitRule()
    {
    }

    public RateLimitRule(int permit, TimeSpan window)
    {
        Permit = permit;
        Window = window;
    }

    public int Permit { get; set; }

    public TimeSpan Window { get; set; }
}

/// <summary>Spec 12's table, bound from <c>RateLimits</c> so a test or a deployment can tighten or relax a policy.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public bool Enabled { get; set; } = true;

    public RateLimitRule Global { get; set; } = new(300, TimeSpan.FromMinutes(1));

    public RateLimitRule Auth { get; set; } = new(10, TimeSpan.FromMinutes(15));

    public RateLimitRule QueueJoin { get; set; } = new(5, TimeSpan.FromMinutes(1));

    public RateLimitRule QueueStatus { get; set; } = new(60, TimeSpan.FromMinutes(1));

    public RateLimitRule Holds { get; set; } = new(20, TimeSpan.FromMinutes(1));

    public RateLimitRule Checkout { get; set; } = new(10, TimeSpan.FromMinutes(1));

    public RateLimitRule For(string policy) => policy switch
    {
        RateLimitPolicies.Global => Global,
        RateLimitPolicies.Auth => Auth,
        RateLimitPolicies.QueueJoin => QueueJoin,
        RateLimitPolicies.QueueStatus => QueueStatus,
        RateLimitPolicies.Holds => Holds,
        RateLimitPolicies.Checkout => Checkout,
        _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown rate-limit policy."),
    };
}
