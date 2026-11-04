using System.Text.Json;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Idempotency;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Idempotency;

/// <summary>
/// One Redis string per (user, key): <c>P|fingerprint</c> while the first request runs (SET NX, so exactly one wins),
/// then the stored response as JSON. Both carry the TTL.
/// </summary>
public sealed class RedisIdempotencyStore(IConnectionMultiplexer redis, RedisKeys keys) : IIdempotencyStore
{
    private const string PendingPrefix = "P|";

    private sealed record Stored(string Fingerprint, int Status, string ContentType, string Body);

    public Task<IdempotencyBegin> BeginAsync(Guid userId, Guid key, string fingerprint, TimeSpan ttl, CancellationToken ct) =>
        GuardAsync<IdempotencyBegin>(async () =>
        {
            var db = redis.GetDatabase();
            var redisKey = keys.Idempotency(userId, key);
            if (await db.StringSetAsync(redisKey, PendingPrefix + fingerprint, ttl, When.NotExists))
            {
                return new IdempotencyBegin.Started();
            }

            var value = (string?)await db.StringGetAsync(redisKey);
            if (value is null)
            {
                // Expired or abandoned between the two calls: claim it again.
                return await db.StringSetAsync(redisKey, PendingPrefix + fingerprint, ttl, When.NotExists)
                    ? new IdempotencyBegin.Started()
                    : new IdempotencyBegin.InProgress();
            }

            if (value.StartsWith(PendingPrefix, StringComparison.Ordinal))
            {
                return value[PendingPrefix.Length..] == fingerprint ? new IdempotencyBegin.InProgress() : new IdempotencyBegin.Mismatch();
            }

            var stored = JsonSerializer.Deserialize<Stored>(value)!;
            return stored.Fingerprint == fingerprint
                ? new IdempotencyBegin.Replay(stored.Status, stored.ContentType, Convert.FromBase64String(stored.Body))
                : new IdempotencyBegin.Mismatch();
        });

    public Task CompleteAsync(Guid userId, Guid key, string fingerprint, int status, string contentType, byte[] body, TimeSpan ttl, CancellationToken ct) =>
        GuardAsync(() => redis.GetDatabase().StringSetAsync(
            keys.Idempotency(userId, key),
            JsonSerializer.Serialize(new Stored(fingerprint, status, contentType, Convert.ToBase64String(body))),
            ttl));

    public Task AbandonAsync(Guid userId, Guid key, CancellationToken ct) =>
        GuardAsync(() => redis.GetDatabase().KeyDeleteAsync(keys.Idempotency(userId, key)));

    private static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            throw new IdempotencyUnavailableException("Idempotency keys could not reach Redis.", ex);
        }
    }
}
