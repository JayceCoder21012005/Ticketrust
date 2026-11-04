using System.Globalization;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Checkout commit and database reconciliation.</summary>
public sealed partial class RedisInventoryStore
{
    public Task CommitAsync(Guid eventId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) =>
        GuardAsync(() => InventoryScripts.CommitAsync(redis.GetDatabase(), keys, eventId, holdId, userId, seats, ga, eventGaTiers));

    public Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var e = seed.EventId;
            var tiers = seed.GaCapacity.Keys.Order().ToArray();
            var buyers = seed.BoughtByUser.Keys.ToArray();
            RedisKey[] redisKeys =
            [
                keys.InvSold(e), keys.InvMeta(e), keys.InvVersion(e), keys.InvGaHeld(e),
                .. InventoryScripts.GaKeys(keys, e, tiers),
                .. buyers.Select(u => keys.InvBought(e, u)),
            ];
            var result = (RedisResult[])(await LuaScripts.RunAsync(
                redis.GetDatabase(),
                "inventory_reconcile",
                redisKeys,
                [
                    InventoryScripts.SeatsCsv(seed.SoldSeats), seed.SeatCount, InventoryScripts.TiersCsv(tiers),
                    string.Join(',', tiers.Select(t => seed.GaCapacity[t])),
                    string.Join(',', tiers.Select(t => seed.GaSold.GetValueOrDefault(t))),
                    string.Join(',', buyers.Select(u => seed.BoughtByUser[u])),
                    keys.InvChanges(e).ToString(),
                ]))!;
            return ToReport(result);
        });

    private static ReconcileReport ToReport(RedisResult[] result)
    {
        var fixes = new Dictionary<Guid, GaFix>();
        for (var i = 2; i + 2 < result.Length; i += 3)
        {
            fixes[Guid.Parse((string)result[i]!)] = new GaFix(
                int.Parse((string)result[i + 1]!, CultureInfo.InvariantCulture),
                int.Parse((string)result[i + 2]!, CultureInfo.InvariantCulture));
        }

        return new ReconcileReport((int)result[0], (int)result[1], fixes);
    }
}
