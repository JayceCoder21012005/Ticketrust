using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Redis implementation of the inventory (spec 07). Each mutation is one Lua script; reads are plain commands.</summary>
public sealed partial class RedisInventoryStore(IConnectionMultiplexer redis, RedisKeys keys, TimeProvider time) : IInventoryStore
{
    public Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            // userhold can change between this read and the script (another tab, the sweeper): the script then
            // answers STALE without writing, and one fresh attempt settles it.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var current = await db.StringGetAsync(keys.InvUserHold(request.EventId, request.UserId));
                var expectedOld = Guid.TryParse((string?)current, out var old) ? old : (Guid?)null;
                var holdId = Guid.CreateVersion7();
                var result = await InventoryScripts.CreateAsync(db, keys, request, holdId, expectedOld);
                if ((string)result[0]! != "STALE")
                {
                    return ToOutcome(result, holdId);
                }
            }

            return HoldOutcome.Failed(HoldStatus.HasHold);
        });

    public Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            var owner = await db.HashGetAsync(keys.InvHold(eventId, holdId), "userId");
            var userId = Guid.TryParse((string?)owner, out var id) ? id : (Guid?)null;
            var result = await InventoryScripts.ReleaseAsync(db, keys, eventId, holdId, userId, eventGaTiers);
            return (string)result[0]! == "OK";
        });

    public Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            if (!Guid.TryParse((string?)await db.StringGetAsync(keys.InvUserHold(eventId, userId)), out var holdId))
            {
                return null;
            }

            var fields = (await db.HashGetAllAsync(keys.InvHold(eventId, holdId))).ToDictionary(f => (string)f.Name!, f => (string)f.Value!);
            if (!fields.TryGetValue("expiresAt", out var expiresMs) || !Guid.TryParse(fields.GetValueOrDefault("userId"), out var owner) || owner != userId)
            {
                return null;
            }

            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(expiresMs, System.Globalization.CultureInfo.InvariantCulture));
            return expiresAt <= time.GetUtcNow()
                ? null
                : new ActiveHold(holdId, userId, ParseSeats(fields.GetValueOrDefault("seats")), ParseGa(fields.GetValueOrDefault("ga")), expiresAt);
        });

    public Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            var held = db.HashKeysAsync(keys.InvHeld(eventId));
            var sold = db.SetMembersAsync(keys.InvSold(eventId));
            var ga = db.StringGetAsync(InventoryScripts.GaKeys(keys, eventId, eventGaTiers));
            var version = db.StringGetAsync(keys.InvVersion(eventId));
            var warm = db.HashGetAsync(keys.InvMeta(eventId), "warm");
            await Task.WhenAll(held, sold, ga, version, warm);

            var remaining = new Dictionary<Guid, int>();
            for (var i = 0; i < eventGaTiers.Count; i++)
            {
                remaining[eventGaTiers[i]] = (int?)ga.Result[i] ?? 0;
            }

            return new InventorySnapshot(
                (long?)version.Result ?? 0,
                [.. held.Result.Select(v => (int)v).Order()],
                [.. sold.Result.Select(v => (int)v).Order()],
                remaining,
                warm.Result == "1");
        });

    public Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct) =>
        GuardAsync<IReadOnlyList<Guid>>(async () =>
        {
            var due = await redis.GetDatabase().SortedSetRangeByScoreAsync(
                keys.InvExpiry(eventId), double.NegativeInfinity, now.ToUnixTimeMilliseconds(), take: limit);
            return [.. due.Select(v => Guid.Parse((string)v!))];
        });

    internal static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // RedisTimeoutException derives from TimeoutException, not RedisException: both mean "Redis is not answering".
            throw new InventoryUnavailableException("The inventory could not reach Redis.", ex);
        }
    }

    private static HoldOutcome ToOutcome(RedisResult[] result, Guid holdId) => (string)result[0]! switch
    {
        "OK" => new HoldOutcome(HoldStatus.Ok, holdId, DateTimeOffset.FromUnixTimeMilliseconds(long.Parse((string)result[1]!, System.Globalization.CultureInfo.InvariantCulture)), [], null),
        "SEAT_UNAVAILABLE" => HoldOutcome.Failed(HoldStatus.SeatUnavailable, [.. result.Skip(1).Select(r => int.Parse((string)r!, System.Globalization.CultureInfo.InvariantCulture)).Order()]),
        "GA_INSUFFICIENT" => HoldOutcome.Failed(HoldStatus.GaInsufficient, tier: Guid.Parse((string)result[1]!)),
        "LIMIT_EXCEEDED" => HoldOutcome.Failed(HoldStatus.LimitExceeded),
        "HAS_HOLD" => HoldOutcome.Failed(HoldStatus.HasHold),
        var other => throw new InvalidOperationException($"hold_create returned {other}."),
    };

    internal static IReadOnlyList<int> ParseSeats(string? csv) =>
        string.IsNullOrEmpty(csv) ? [] : [.. csv.Split(',').Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture))];

    internal static IReadOnlyList<GaLine> ParseGa(string? csv) =>
        string.IsNullOrEmpty(csv)
            ? []
            : [.. csv.Split(',').Select(p => p.Split(':')).Select(p => new GaLine(Guid.Parse(p[0]), int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture)))];
}
