using StackExchange.Redis;
using TicketRush.Application.Abstractions.Resale;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Resale;

/// <summary>
/// <c>resale:{evt}:listing:&lt;id&gt;</c> → buyer id, <c>NX PX 600000</c> (spec 14). The event id is the hash tag, like the
/// inventory keys. Releasing compares and deletes in one script, so a late release never frees another buyer's key.
/// </summary>
public sealed class RedisResaleReservations(IConnectionMultiplexer redis, RedisKeys keys) : IResaleReservations
{
    private const string CompareAndDelete = "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end return 0";

    public Task<bool> TryReserveAsync(Guid eventId, Guid listingId, Guid buyerId, TimeSpan ttl, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            var key = Key(eventId, listingId);
            var buyer = buyerId.ToString("N");
            if (await db.StringSetAsync(key, buyer, ttl, When.NotExists))
            {
                return true;
            }

            return (string?)await db.StringGetAsync(key) == buyer;
        });

    public Task ReleaseAsync(Guid eventId, Guid listingId, Guid buyerId, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
            await redis.GetDatabase().ScriptEvaluateAsync(CompareAndDelete, [Key(eventId, listingId)], [buyerId.ToString("N")]));

    internal RedisKey Key(Guid eventId, Guid listingId) => $"{keys.Prefix}resale:{{{eventId:N}}}:listing:{listingId:N}";
}
