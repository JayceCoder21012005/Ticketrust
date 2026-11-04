using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// One admitter tick per call (spec 06): the worker calls it every second for each open sale. It expires finished
/// shoppers, lets in at most <c>min(rate, free capacity)</c> more buyers, and keeps the observed admission rate
/// (EMA over about 60 s) that the status endpoint turns into an estimated wait.
/// </summary>
public sealed class WaitingRoomAdmitter(
    IConnectionMultiplexer redis,
    RedisKeys keys,
    IOptions<QueueOptions> options,
    TimeProvider time)
{
    /// <summary>EMA weight for a 60-sample window: 2 / (60 + 1).</summary>
    private const double Alpha = 2d / 61d;

    public async Task<AdmitTick> TickAsync(Guid phaseId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var tick = await QueueScripts.AdmitAsync(
            db, keys, phaseId, time.GetUtcNow(), options.Value.AdmissionRatePerSecond, options.Value.MaxActiveShoppers);

        // Idle seconds (nobody waiting) say nothing about throughput, so they are not sampled.
        var hadBacklog = tick.Status == nameof(Application.Abstractions.Queue.RoomStatus.Open) && tick.Seq > tick.AdmittedBefore;
        if (hadBacklog)
        {
            await UpdateRateAsync(db, phaseId, tick.AdmittedAfter - tick.AdmittedBefore);
        }

        return tick;
    }

    public Task CloseAsync(Guid phaseId, CancellationToken ct) =>
        redis.GetDatabase().HashSetAsync(keys.QueueState(phaseId), "status", nameof(Application.Abstractions.Queue.RoomStatus.Closed));

    private async Task UpdateRateAsync(IDatabase db, Guid phaseId, long admittedThisTick)
    {
        var stateKey = keys.QueueState(phaseId);
        var previous = await db.HashGetAsync(stateKey, "rateEma");
        var old = previous.HasValue && double.TryParse((string?)previous, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
        var next = old <= 0 ? admittedThisTick : old + (Alpha * (admittedThisTick - old));
        await db.HashSetAsync(stateKey, "rateEma", next.ToString("R", CultureInfo.InvariantCulture));
    }
}
