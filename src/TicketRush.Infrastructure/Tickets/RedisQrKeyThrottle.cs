using StackExchange.Redis;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Tickets;

/// <summary>
/// Fixed 60 s window, 10 requests per user, counted by <c>qr_key_throttle.lua</c> (spec 10). Any Redis failure surfaces as
/// <see cref="ThrottleUnavailableException"/>: callers fail closed. Spec 12 replaces this with its named sliding-window policy.
/// </summary>
public sealed class RedisQrKeyThrottle(IConnectionMultiplexer redis, RedisKeys keys) : IQrKeyThrottle
{
    public const int Limit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public async Task<ThrottleResult> TryAcquireAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var reply = (RedisResult[])(await LuaScripts.RunAsync(
                redis.GetDatabase(),
                "qr_key_throttle",
                [keys.QrKeyThrottle(userId)],
                [(long)Window.TotalSeconds]).WaitAsync(ct))!;
            var count = (long)reply[0];
            var ttl = TimeSpan.FromMilliseconds((long)reply[1]);
            return count <= Limit ? new ThrottleResult(true, TimeSpan.Zero) : new ThrottleResult(false, ttl > TimeSpan.Zero ? ttl : Window);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // RedisTimeoutException derives from TimeoutException, not RedisException: both mean "Redis is not answering".
            throw new ThrottleUnavailableException("The QR key throttle could not reach Redis.", ex);
        }
    }
}
