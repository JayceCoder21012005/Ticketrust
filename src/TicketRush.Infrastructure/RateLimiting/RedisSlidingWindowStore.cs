using StackExchange.Redis;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.RateLimiting;

/// <summary><c>sliding_window.lua</c> behind <see cref="ISlidingWindowStore"/>: one atomic call per request.</summary>
public sealed class RedisSlidingWindowStore(IConnectionMultiplexer redis, RedisKeys keys) : ISlidingWindowStore
{
    public async Task<WindowResult> HitAsync(string policy, string partition, int limit, TimeSpan window, CancellationToken ct)
    {
        var reply = (RedisResult[])(await LuaScripts.RunAsync(
            redis.GetDatabase(),
            "sliding_window",
            [keys.RateLimit(policy, partition)],
            [limit, (long)window.TotalMilliseconds, Guid.NewGuid().ToString("N")]).WaitAsync(ct))!;
        return (long)reply[0] == 1 ? new WindowResult(true, TimeSpan.Zero) : new WindowResult(false, TimeSpan.FromMilliseconds((long)reply[1]));
    }
}
