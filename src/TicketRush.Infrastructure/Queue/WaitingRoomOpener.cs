using System.Security.Cryptography;
using StackExchange.Redis;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// Opens a sale at T0 (spec 06): everyone who joined the pre-queue gets a uniformly random place, then the room
/// opens FIFO. Safe to re-run after a leader failover: numbering is atomic per batch and resumes with whoever is
/// still in <c>pre</c>.
/// </summary>
public sealed class WaitingRoomOpener(IConnectionMultiplexer redis, RedisKeys keys)
{
    private const int BatchSize = 1_000;

    /// <returns>How many pre-queue members this call numbered; 0 when there was nothing to open.</returns>
    public async Task<long> OpenAsync(Guid phaseId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        if (!await QueueScripts.BeginOpenAsync(db, keys, phaseId))
        {
            return 0;
        }

        var members = (await db.SetMembersAsync(keys.QueueOpening(phaseId))).Select(m => m.ToString()).ToArray();
        Shuffle(members);

        long numbered = 0;
        foreach (var batch in members.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();
            numbered += await QueueScripts.AssignBatchAsync(db, keys, phaseId, batch);
        }

        await QueueScripts.FinalizeOpenAsync(db, keys, phaseId);
        return numbered;
    }

    /// <summary>Fisher–Yates with a cryptographic source, so no one can predict or bias their place.</summary>
    internal static void Shuffle<T>(Span<T> items)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
