using System.Threading.RateLimiting;
using TicketRush.Api.Common.Http;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Common.RateLimiting;

public static class RateLimitingSetup
{
    /// <summary>Spec 12: one Redis-backed limiter for every policy; a refusal is the standard 429 <c>rate_limited</c> problem with <c>Retry-After</c>.</summary>
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>().BindConfiguration(RateLimitOptions.SectionName);
        services.AddSingleton<RedisRateLimiter>();
        services.AddRateLimiter(_ => { });
        services.AddOptions<Microsoft.AspNetCore.RateLimiting.RateLimiterOptions>()
            .Configure<RedisRateLimiter>((limiter, redis) =>
            {
                limiter.GlobalLimiter = redis;
                limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                limiter.OnRejected = (context, _) =>
                {
                    var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : TimeSpan.FromSeconds(1);
                    return new ValueTask(CommonErrors.RateLimited(retryAfter).ToProblem().ExecuteAsync(context.HttpContext));
                };
            });
        return services;
    }
}
