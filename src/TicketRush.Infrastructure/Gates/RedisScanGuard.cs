using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Gates;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Gates;

/// <summary>
/// Per-staff limit of 5 scans per second and the 3-second memory of the last answer per payload (spec 11), in Redis. The payload
/// is hashed before it is part of a key. Redis failures on <see cref="EnterAsync"/> surface as <see cref="ThrottleUnavailableException"/>
/// (fail closed); <see cref="RememberAsync"/> only logs the exception type.
/// </summary>
public sealed class RedisScanGuard(IConnectionMultiplexer redis, RedisKeys keys, ILogger<RedisScanGuard> logger) : IScanGuard
{
    private const int WindowMs = 1000;

    public async Task<ScanGate> EnterAsync(Guid staffUserId, string payload, CancellationToken ct)
    {
        try
        {
            var reply = (RedisResult[])(await LuaScripts.RunAsync(
                redis.GetDatabase(),
                "scan_guard",
                [keys.ScanRate(staffUserId), keys.ScanRepeat(staffUserId, Fingerprint(payload))],
                [IScanGuard.ScansPerSecond, WindowMs]).WaitAsync(ct))!;
            if ((long)reply[0] == 0)
            {
                return new ScanGate(false, TimeSpan.FromMilliseconds((long)reply[1]), null);
            }

            var previous = reply[2].IsNull ? null : (string?)reply[2];
            return new ScanGate(true, TimeSpan.Zero, previous);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            throw new ThrottleUnavailableException("The scan guard could not reach Redis.", ex);
        }
    }

    public async Task RememberAsync(Guid staffUserId, string payload, string answerJson, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase()
                .StringSetAsync(keys.ScanRepeat(staffUserId, Fingerprint(payload)), answerJson, IScanGuard.RepeatWindow)
                .WaitAsync(ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            logger.LogWarning("Could not remember a scan answer ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private static string Fingerprint(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
