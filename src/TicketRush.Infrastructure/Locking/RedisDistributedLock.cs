using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Locking;

/// <summary>
/// Leader lock: <c>SET key token NX PX ttl</c>, renewed every ttl/3 while held. Only the owner (same token)
/// can renew or release, so a holder that paused past its TTL can never delete a successor's lock.
/// </summary>
public sealed partial class RedisDistributedLock(
    IConnectionMultiplexer redis,
    RedisKeys keys,
    ILogger<RedisDistributedLock> logger) : IDistributedLock
{
    // KEYS[1] = lock key; ARGV[1] = owner token; ARGV[2] = ttl in ms.
    // Returns 1 when the caller still owns the lock and its TTL was extended, 0 otherwise.
    internal const string RenewScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('PEXPIRE', KEYS[1], ARGV[2])
        end
        return 0
        """;

    // KEYS[1] = lock key; ARGV[1] = owner token.
    // Returns 1 when the caller owned the lock and deleted it, 0 when it was already gone or owned by someone else.
    internal const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then
            return redis.call('DEL', KEYS[1])
        end
        return 0
        """;

    public async Task<IDistributedLockHandle?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(ttl, TimeSpan.FromSeconds(1));

        var key = keys.Lock(name);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var db = redis.GetDatabase();

        var acquired = await db.StringSetAsync(key, token, ttl, When.NotExists);
        return acquired ? new Handle(db, key, token, name, ttl, logger) : null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost distributed lock {LockName}")]
    private static partial void LogLockLost(ILogger logger, string lockName, Exception? exception);

    private sealed class Handle : IDistributedLockHandle
    {
        private readonly IDatabase _db;
        private readonly RedisKey _key;
        private readonly string _token;
        private readonly TimeSpan _ttl;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lockLost = new();
        private readonly CancellationTokenSource _stopRenewal = new();
        private readonly Task _renewLoop;

        public Handle(IDatabase db, RedisKey key, string token, string name, TimeSpan ttl, ILogger logger)
        {
            _db = db;
            _key = key;
            _token = token;
            _ttl = ttl;
            _logger = logger;
            Name = name;
            _renewLoop = RenewLoopAsync();
        }

        public string Name { get; }

        public CancellationToken LockLost => _lockLost.Token;

        public async ValueTask DisposeAsync()
        {
            await _stopRenewal.CancelAsync();
            await _renewLoop;

            try
            {
                await _db.ScriptEvaluateAsync(ReleaseScript, [_key], [_token]);
            }
            catch (RedisException)
            {
                // The TTL releases the lock anyway; never fail shutdown because Redis is down.
            }

            _stopRenewal.Dispose();
            _lockLost.Dispose();
        }

        private async Task RenewLoopAsync()
        {
            using var timer = new PeriodicTimer(_ttl / 3);
            try
            {
                while (await timer.WaitForNextTickAsync(_stopRenewal.Token))
                {
                    var renewed = (long)await _db.ScriptEvaluateAsync(RenewScript, [_key], [_token, (long)_ttl.TotalMilliseconds]);
                    if (renewed == 0)
                    {
                        MarkLost(null);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown via DisposeAsync.
            }
            catch (RedisException ex)
            {
                MarkLost(ex);
            }
        }

        private void MarkLost(Exception? exception)
        {
            LogLockLost(_logger, Name, exception);
            _lockLost.Cancel();
        }
    }
}
