using StackExchange.Redis;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Typed wrappers over the inventory Lua scripts (key order must match each script's header).</summary>
public static class InventoryScripts
{
    /// <summary>Seats csv in ascending order (C9).</summary>
    public static string SeatsCsv(IEnumerable<int> seats) => string.Join(',', seats.Distinct().Order());

    public static string GaCsv(IEnumerable<GaLine> ga) =>
        string.Join(',', ga.Where(l => l.Quantity > 0).GroupBy(l => l.TierId).Select(g => $"{g.Key:N}:{g.Sum(l => l.Quantity)}"));

    public static string TiersCsv(IReadOnlyList<Guid> tiers) => string.Join(',', tiers.Select(t => t.ToString("N")));

    public static RedisKey[] GaKeys(RedisKeys keys, Guid eventId, IReadOnlyList<Guid> tiers) => [.. tiers.Select(t => keys.InvGa(eventId, t))];

    /// <param name="expectedOld">The holdId read from userhold, or null when the user had none.</param>
    public static async Task<RedisResult[]> CreateAsync(
        IDatabase db, RedisKeys keys, HoldRequest request, Guid holdId, Guid? expectedOld)
    {
        var e = request.EventId;
        RedisKey[] fixedKeys =
        [
            keys.InvSold(e), keys.InvHeld(e), keys.InvExpiry(e), keys.InvUserHold(e, request.UserId), keys.InvBought(e, request.UserId),
            keys.InvVersion(e), keys.InvGaHeld(e), keys.InvHold(e, holdId), keys.InvHold(e, expectedOld ?? Guid.Empty),
        ];
        return (RedisResult[])(await LuaScripts.RunAsync(
            db,
            "hold_create",
            [.. fixedKeys, .. GaKeys(keys, e, request.EventGaTiers)],
            [
                holdId.ToString("N"), request.UserId.ToString("N"), SeatsCsv(request.SeatIndexes), GaCsv(request.Ga),
                request.MaxTickets, request.ReplaceExisting ? 1 : 0, request.Now.ToUnixTimeMilliseconds(),
                (long)request.Ttl.TotalMilliseconds, expectedOld?.ToString("N") ?? string.Empty,
                keys.InvChanges(e).ToString(), TiersCsv(request.EventGaTiers),
            ]))!;
    }

    public static async Task<RedisResult[]> CommitAsync(
        IDatabase db, RedisKeys keys, Guid eventId, Guid holdId, Guid userId, IEnumerable<int> seats, IEnumerable<GaLine> ga, IReadOnlyList<Guid> tiers)
    {
        RedisKey[] fixedKeys =
        [
            keys.InvSold(eventId), keys.InvHeld(eventId), keys.InvExpiry(eventId), keys.InvUserHold(eventId, userId),
            keys.InvBought(eventId, userId), keys.InvVersion(eventId), keys.InvGaHeld(eventId), keys.InvHold(eventId, holdId),
        ];
        return (RedisResult[])(await LuaScripts.RunAsync(
            db,
            "inventory_commit",
            [.. fixedKeys, .. GaKeys(keys, eventId, tiers)],
            [holdId.ToString("N"), userId.ToString("N"), SeatsCsv(seats), GaCsv(ga), keys.InvChanges(eventId).ToString(), TiersCsv(tiers)]))!;
    }

    public static async Task<RedisResult[]> ReleaseAsync(
        IDatabase db, RedisKeys keys, Guid eventId, Guid holdId, Guid? userId, IReadOnlyList<Guid> tiers)
    {
        RedisKey[] fixedKeys =
        [
            keys.InvHeld(eventId), keys.InvExpiry(eventId), keys.InvUserHold(eventId, userId ?? Guid.Empty),
            keys.InvVersion(eventId), keys.InvGaHeld(eventId), keys.InvHold(eventId, holdId),
        ];
        return (RedisResult[])(await LuaScripts.RunAsync(
            db,
            "hold_release",
            [.. fixedKeys, .. GaKeys(keys, eventId, tiers)],
            [holdId.ToString("N"), keys.InvChanges(eventId).ToString(), TiersCsv(tiers)]))!;
    }
}
