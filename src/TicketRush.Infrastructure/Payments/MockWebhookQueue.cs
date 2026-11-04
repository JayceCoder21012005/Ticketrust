using System.Text.Json;
using StackExchange.Redis;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Payments;

/// <summary>A delivery taken from the queue; <see cref="Member"/> is the exact ZSET member to acknowledge or replace.</summary>
public sealed record ClaimedDelivery(MockDelivery Delivery, RedisValue Member);

/// <summary>
/// The mock gateway's webhook outbox: a ZSET of JSON deliveries scored by due time (unix ms). Claiming does not remove
/// an entry, it pushes its score out by a lease; the entry is removed only when the receiver acknowledged it. A worker
/// that dies mid-delivery therefore re-sends after the lease (at-least-once, which the webhook handler dedupes).
/// </summary>
public sealed class MockWebhookQueue(IConnectionMultiplexer redis, RedisKeys keys)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task EnqueueAsync(MockDelivery delivery, DateTimeOffset due) =>
        redis.GetDatabase().SortedSetAddAsync(keys.PayMockDeliveries, Serialize(delivery), Score(due));

    /// <summary>Queues the delivery inside an open transaction (completing a session and its webhooks are one atomic step).</summary>
    internal void StageEnqueue(ITransaction transaction, MockDelivery delivery, DateTimeOffset due) =>
        _ = transaction.SortedSetAddAsync(keys.PayMockDeliveries, Serialize(delivery), Score(due));

    public async Task<long> CountAsync() => await redis.GetDatabase().SortedSetLengthAsync(keys.PayMockDeliveries);

    /// <summary>Takes up to <paramref name="batch"/> deliveries due at <paramref name="now"/> and hides them for <paramref name="lease"/>.</summary>
    public async Task<IReadOnlyList<ClaimedDelivery>> ClaimDueAsync(DateTimeOffset now, int batch, TimeSpan lease)
    {
        var db = redis.GetDatabase();
        var members = await db.SortedSetRangeByScoreAsync(keys.PayMockDeliveries, double.NegativeInfinity, Score(now), take: batch);
        var claimed = new List<ClaimedDelivery>(members.Length);
        foreach (var member in members)
        {
            // XX: only moves an entry that still exists (it adds nothing, so the reply is always false).
            await db.SortedSetAddAsync(keys.PayMockDeliveries, member, Score(now + lease), SortedSetWhen.Exists);
            if (Parse(member) is { } delivery)
            {
                claimed.Add(new ClaimedDelivery(delivery, member));
            }
            else
            {
                await db.SortedSetRemoveAsync(keys.PayMockDeliveries, member);
            }
        }

        return claimed;
    }

    /// <summary>The receiver accepted the delivery (or it is dropped for good).</summary>
    public Task AcknowledgeAsync(ClaimedDelivery claimed) =>
        redis.GetDatabase().SortedSetRemoveAsync(keys.PayMockDeliveries, claimed.Member);

    /// <summary>Swaps the entry for the next attempt, due at <paramref name="due"/>.</summary>
    public async Task RescheduleAsync(ClaimedDelivery claimed, DateTimeOffset due)
    {
        var transaction = redis.GetDatabase().CreateTransaction();
        _ = transaction.SortedSetRemoveAsync(keys.PayMockDeliveries, claimed.Member);
        _ = transaction.SortedSetAddAsync(keys.PayMockDeliveries, Serialize(claimed.Delivery with { Attempt = claimed.Delivery.Attempt + 1 }), Score(due));
        await transaction.ExecuteAsync();
    }

    private static double Score(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    private static string Serialize(MockDelivery delivery) => JsonSerializer.Serialize(delivery, Json);

    private static MockDelivery? Parse(RedisValue member)
    {
        try
        {
            return JsonSerializer.Deserialize<MockDelivery>((string)member!, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
