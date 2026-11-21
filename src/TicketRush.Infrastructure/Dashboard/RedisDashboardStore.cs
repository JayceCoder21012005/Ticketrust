using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Dashboard;

/// <summary>
/// <c>dash:{evt}</c> (spec 15): a HASH of counters (<c>sold</c>, <c>revenue</c>, <c>paid</c>, <c>created</c>, <c>held</c>,
/// <c>sold:&lt;tier&gt;</c>, <c>rev:&lt;tier&gt;</c>), a LIST of the last 20 paid orders, a marker per counted order (7 days) and a ZSET
/// of viewers (connection → last seen). The event id is the hash tag so each script touches one slot. A global SET names the events
/// with viewers so the broadcaster need not scan.
/// </summary>
public sealed partial class RedisDashboardStore(IConnectionMultiplexer redis, RedisKeys keys, ILogger<RedisDashboardStore> logger) : IDashboardStore
{
    public const int RecentCount = 20;

    private static readonly TimeSpan MarkerTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string PaidScript = """
        if not redis.call('SET', KEYS[2], '1', 'NX', 'EX', ARGV[1]) then return 0 end
        redis.call('HINCRBY', KEYS[1], 'sold', ARGV[2])
        redis.call('HINCRBY', KEYS[1], 'revenue', ARGV[3])
        redis.call('HINCRBY', KEYS[1], 'paid', 1)
        for i = 5, #ARGV, 3 do
          redis.call('HINCRBY', KEYS[1], 'sold:' .. ARGV[i], ARGV[i + 1])
          redis.call('HINCRBY', KEYS[1], 'rev:' .. ARGV[i], ARGV[i + 2])
        end
        redis.call('LPUSH', KEYS[3], ARGV[4])
        redis.call('LTRIM', KEYS[3], 0, 19)
        return 1
        """;

    // ARGV[1] = number of field/value pairs, then the pairs, then the recent orders oldest last.
    private const string OverwriteScript = """
        local held = redis.call('HGET', KEYS[1], 'held')
        redis.call('DEL', KEYS[1], KEYS[2])
        if held then redis.call('HSET', KEYS[1], 'held', held) end
        local pairs = tonumber(ARGV[1])
        for i = 2, 1 + pairs * 2, 2 do
          redis.call('HSET', KEYS[1], ARGV[i], ARGV[i + 1])
        end
        for i = 2 + pairs * 2, #ARGV do
          redis.call('RPUSH', KEYS[2], ARGV[i])
        end
        return 1
        """;

    public Task<bool> RecordPaidAsync(Guid eventId, RecentOrder order, IReadOnlyList<PaidTierLine> lines, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var args = new List<RedisValue>
            {
                (long)MarkerTtl.TotalSeconds,
                lines.Sum(l => l.Tickets),
                lines.Sum(l => l.Revenue),
                JsonSerializer.Serialize(order, Json),
            };
            foreach (var line in lines)
            {
                args.Add(line.TierId.ToString("N"));
                args.Add(line.Tickets);
                args.Add(line.Revenue);
            }

            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                PaidScript, [Hash(eventId), Marker(eventId, order.OrderId), Recent(eventId)], [.. args]);
            return (long)result == 1;
        });

    public Task RecordHeldAsync(Guid eventId, CancellationToken ct) => BestEffortAsync(eventId, "held");

    public Task RecordOrderCreatedAsync(Guid eventId, CancellationToken ct) => BestEffortAsync(eventId, "created");

    public Task<DashboardCounters> ReadAsync(Guid eventId, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            var fields = (await db.HashGetAllAsync(Hash(eventId))).ToDictionary(e => e.Name.ToString(), e => (long)e.Value);
            var recent = await db.ListRangeAsync(Recent(eventId), 0, RecentCount - 1);
            return new DashboardCounters(
                fields.GetValueOrDefault("sold"),
                fields.GetValueOrDefault("revenue"),
                fields.GetValueOrDefault("created"),
                fields.GetValueOrDefault("paid"),
                fields.GetValueOrDefault("held"),
                ByTier(fields, "sold:"),
                ByTier(fields, "rev:"),
                [.. recent.Select(v => JsonSerializer.Deserialize<RecentOrder>(v.ToString(), Json)!)]);
        });

    public Task OverwriteAsync(Guid eventId, DashboardTotals totals, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var pairs = new List<RedisValue>
            {
                "sold", totals.Sold, "revenue", totals.Revenue, "created", totals.OrdersCreated, "paid", totals.Paid,
            };
            foreach (var (tier, sold) in totals.SoldByTier)
            {
                pairs.Add($"sold:{tier:N}");
                pairs.Add(sold);
            }

            foreach (var (tier, revenue) in totals.RevenueByTier)
            {
                pairs.Add($"rev:{tier:N}");
                pairs.Add(revenue);
            }

            RedisValue[] args = [pairs.Count / 2, .. pairs, .. totals.RecentOrders.Take(RecentCount).Select(o => (RedisValue)JsonSerializer.Serialize(o, Json))];
            await redis.GetDatabase().ScriptEvaluateAsync(OverwriteScript, [Hash(eventId), Recent(eventId)], args);
            return true;
        });

    public Task<QueueFigures> ReadQueueAsync(IReadOnlyList<Guid> phases, DateTimeOffset now, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            long joined = 0, admitted = 0, waiting = 0, active = 0;
            double rate = 0;
            foreach (var phase in phases)
            {
                var seq = (long)await db.StringGetAsync(keys.QueueSeq(phase));
                var upTo = (long)await db.StringGetAsync(keys.QueueAdmitted(phase));
                var early = await db.SetLengthAsync(keys.QueuePre(phase)) + await db.SetLengthAsync(keys.QueueOpening(phase));
                joined += seq + early;
                admitted += Math.Min(upTo, seq);
                waiting += Math.Max(0, seq - upTo) + early;
                active += await db.SortedSetLengthAsync(keys.QueueActive(phase), now.ToUnixTimeMilliseconds(), double.PositiveInfinity);
                var ema = await db.HashGetAsync(keys.QueueState(phase), "rateEma");
                rate += ema.HasValue && double.TryParse(ema.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
            }

            return new QueueFigures(joined, admitted, waiting, active, rate);
        });

    public Task AddViewerAsync(Guid eventId, string connectionId, DateTimeOffset now, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            await db.SortedSetAddAsync(Viewers(eventId), connectionId, now.ToUnixTimeMilliseconds());
            await db.SetAddAsync(Watched, eventId.ToString("N"));
            return true;
        });

    public Task RemoveViewerAsync(Guid eventId, string connectionId, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync(() => redis.GetDatabase().SortedSetRemoveAsync(Viewers(eventId), connectionId));

    public Task<IReadOnlyList<Guid>> WatchedEventsAsync(DateTimeOffset seenSince, CancellationToken ct) =>
        RedisInventoryStore.GuardAsync<IReadOnlyList<Guid>>(async () =>
        {
            var db = redis.GetDatabase();
            var watched = new List<Guid>();
            foreach (var member in await db.SetMembersAsync(Watched))
            {
                var eventId = Guid.Parse(member.ToString());
                await db.SortedSetRemoveRangeByScoreAsync(Viewers(eventId), double.NegativeInfinity, seenSince.ToUnixTimeMilliseconds(), Exclude.Stop);
                if (await db.SortedSetLengthAsync(Viewers(eventId)) > 0)
                {
                    watched.Add(eventId);
                }
                else
                {
                    await db.SetRemoveAsync(Watched, member);
                }
            }

            return watched;
        });

    private async Task BestEffortAsync(Guid eventId, string field)
    {
        try
        {
            await redis.GetDatabase().HashIncrementAsync(Hash(eventId), field);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // A funnel step is a hint; the order path never fails because of it.
            LogBestEffortFailed(logger, field, eventId, ex);
        }
    }

    private static Dictionary<Guid, long> ByTier(Dictionary<string, long> fields, string prefix) =>
        fields.Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && Guid.TryParse(f.Key[prefix.Length..], out _))
            .ToDictionary(f => Guid.Parse(f.Key[prefix.Length..]), f => f.Value);

    internal RedisKey Hash(Guid eventId) => $"{keys.Prefix}dash:{{{eventId:N}}}";

    private RedisKey Marker(Guid eventId, Guid orderId) => $"{keys.Prefix}dash:{{{eventId:N}}}:paid:{orderId:N}";

    private RedisKey Recent(Guid eventId) => $"{keys.Prefix}dash:{{{eventId:N}}}:recent";

    private RedisKey Viewers(Guid eventId) => $"{keys.Prefix}dash:{{{eventId:N}}}:viewers";

    private RedisKey Watched => $"{keys.Prefix}dash:watched";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard counter {Field} of event {EventId} was not counted")]
    private static partial void LogBestEffortFailed(ILogger logger, string field, Guid eventId, Exception exception);
}
