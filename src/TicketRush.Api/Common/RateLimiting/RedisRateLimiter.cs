using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.Common.Auth;
using TicketRush.Application.Abstractions.Security;

namespace TicketRush.Api.Common.RateLimiting;

/// <summary>
/// Spec 12's limiter, plugged into ASP.NET Core's rate-limiting middleware as its global limiter: every request is counted under
/// <c>global</c> (by ip) and under each named policy its endpoint declares (<see cref="RateLimitPolicies.RequireRateLimit{TBuilder}"/>).
/// The windows live in Redis (<see cref="ISlidingWindowStore"/>), so the limits hold across api instances, and this class keeps no
/// per-ip state to clean up. When Redis does not answer it fails open: a limiter outage must not take reads down, and the write
/// paths that need Redis fail closed on their own.
/// </summary>
public sealed partial class RedisRateLimiter(
    ISlidingWindowStore store,
    IOptionsMonitor<RateLimitOptions> options,
    ILogger<RedisRateLimiter> logger) : PartitionedRateLimiter<HttpContext>
{
    private const int MaxBodyPeek = 4096;

    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => null;

    /// <summary>
    /// The middleware tries this synchronous path first and, when it is not acquired, awaits <see cref="AcquireAsyncCore"/>.
    /// Counting needs a Redis round trip, so the answer here is always "not yet".
    /// </summary>
    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount) => NotYet.Instance;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var endpoint = resource.GetEndpoint();
        if (!settings.Enabled || endpoint?.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null || IsProbe(resource.Request.Path))
        {
            return Granted.Instance;
        }

        try
        {
            var ip = resource.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var global = await store.HitAsync(RateLimitPolicies.Global, ip, settings.Global.Permit, settings.Global.Window, cancellationToken);
            if (!global.Allowed)
            {
                return new Refused(global.RetryAfter);
            }

            foreach (var named in endpoint?.Metadata.GetOrderedMetadata<RateLimitPolicies.Metadata>() ?? [])
            {
                var rule = settings.For(named.Policy);
                var partition = await PartitionAsync(named.Policy, resource, ip);
                var result = await store.HitAsync(named.Policy, partition, rule.Permit, rule.Window, cancellationToken);
                if (!result.Allowed)
                {
                    return new Refused(result.RetryAfter);
                }
            }
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // RedisTimeoutException derives from TimeoutException, not RedisException: both mean "Redis is not answering".
            LogSkipped(ex);
        }

        return Granted.Instance;
    }

    // Nothing held of its own: every window lives in Redis.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    private static bool IsProbe(PathString path) => path.StartsWithSegments("/health") || path.StartsWithSegments("/alive");

    private static async ValueTask<string> PartitionAsync(string policy, HttpContext context, string ip)
    {
        if (policy == RateLimitPolicies.Auth)
        {
            return $"{ip}|{await EmailAsync(context)}";
        }

        return context.User.Identity?.IsAuthenticated == true ? $"user:{context.User.UserId():N}" : $"ip:{ip}";
    }

    /// <summary>
    /// The <c>email</c> of a small JSON body, trimmed and lower-cased. The body is buffered and rewound for the endpoint;
    /// anything else (no body, too big, not JSON) counts as no email, so it is limited by ip alone.
    /// </summary>
    private static async ValueTask<string> EmailAsync(HttpContext context)
    {
        var request = context.Request;
        if (!request.HasJsonContentType() || request.ContentLength is not (> 0 and <= MaxBodyPeek))
        {
            return string.Empty;
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.RequestAborted);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("email", out var email)
                && email.ValueKind == JsonValueKind.String
                    ? email.GetString()!.Trim().ToLowerInvariant()
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Rate limiting skipped for this request: Redis did not answer.")]
    private partial void LogSkipped(Exception exception);

    private sealed class Granted : RateLimitLease
    {
        public static readonly Granted Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class NotYet : RateLimitLease
    {
        public static readonly NotYet Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class Refused(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
