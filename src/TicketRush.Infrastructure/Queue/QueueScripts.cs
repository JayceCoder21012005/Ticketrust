using StackExchange.Redis;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Queue;

/// <summary>Typed wrappers over the waiting-room Lua scripts (key order must match each script's header).</summary>
public static class QueueScripts
{
    public static async Task<bool> BeginOpenAsync(IDatabase db, RedisKeys keys, Guid phaseId) =>
        (long)await LuaScripts.RunAsync(db, "queue_begin_open", [keys.QueueState(phaseId), keys.QueuePre(phaseId), keys.QueueOpening(phaseId)], []) == 1;

    public static async Task<long> AssignBatchAsync(IDatabase db, RedisKeys keys, Guid phaseId, IReadOnlyList<string> userIds) =>
        (long)await LuaScripts.RunAsync(
            db,
            "queue_assign_batch",
            [keys.QueueOpening(phaseId), keys.QueueLine(phaseId), keys.QueueSeq(phaseId), keys.QueueState(phaseId)],
            [.. userIds.Select(id => (RedisValue)id)]);

    /// <returns>Late joiners numbered, or -1 when the room was not in Opening.</returns>
    public static async Task<long> FinalizeOpenAsync(IDatabase db, RedisKeys keys, Guid phaseId) =>
        (long)await LuaScripts.RunAsync(
            db,
            "queue_finalize_open",
            [keys.QueueState(phaseId), keys.QueuePre(phaseId), keys.QueueLine(phaseId), keys.QueueSeq(phaseId), keys.QueueOpening(phaseId)],
            []);

    /// <summary>Moves the room to <paramref name="status"/> only from one of <paramref name="from"/>.</summary>
    /// <returns>The status before the call (empty when the room does not exist).</returns>
    public static async Task<string> SetStatusAsync(IDatabase db, RedisKeys keys, Guid phaseId, string status, IReadOnlyList<string> from) =>
        (string)(await LuaScripts.RunAsync(db, "queue_set_status", [keys.QueueState(phaseId)], [status, string.Join(',', from)]))!;

    public static async Task<AdmitTick> AdmitAsync(
        IDatabase db, RedisKeys keys, Guid phaseId, DateTimeOffset now, int ratePerSecond, int maxActive)
    {
        var result = (RedisResult[])(await LuaScripts.RunAsync(
            db,
            "queue_admit",
            [
                keys.QueueState(phaseId), keys.QueueSeq(phaseId), keys.QueueAdmitted(phaseId),
                keys.QueueActive(phaseId), keys.QueueTokens(phaseId), keys.QueueDone(phaseId),
            ],
            [now.ToUnixTimeMilliseconds(), ratePerSecond, maxActive]))!;
        return new AdmitTick((long)result[0], (long)result[1], (long)result[2], (long)result[3], (string)result[4]!);
    }
}

public sealed record AdmitTick(long AdmittedBefore, long AdmittedAfter, long Seq, long ActiveCount, string Status);
