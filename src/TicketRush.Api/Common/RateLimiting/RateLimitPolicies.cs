namespace TicketRush.Api.Common.RateLimiting;

/// <summary>
/// The named policies of spec 12. <c>qr-key</c> (10/min per user) and <c>gate-scan</c> (5/s per staff member) are enforced by their
/// own Redis throttles (<c>IQrKeyThrottle</c>, <c>IScanGuard</c>), which answer the same 429 <c>rate_limited</c>.
/// The rest: <c>global</c> counts every request by ip; the others count by user (by ip when anonymous), <c>auth</c> by ip and email.
/// </summary>
public static class RateLimitPolicies
{
    public const string Global = "global";
    public const string Auth = "auth";
    public const string QueueJoin = "queue-join";
    public const string QueueStatus = "queue-status";
    public const string Holds = "holds";
    public const string Checkout = "checkout";

    /// <summary>Endpoint metadata: this endpoint is also counted under <see cref="Policy"/>.</summary>
    public sealed record Metadata(string Policy);

    /// <summary>Counts the endpoint under a named policy, on top of the <c>global</c> one.</summary>
    public static TBuilder RequireRateLimit<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder => builder.WithMetadata(new Metadata(policy));
}
