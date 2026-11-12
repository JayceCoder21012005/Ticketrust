using System.Globalization;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// Redis-backed waiting room (spec 06). Every mutation is a single Lua script on keys sharing one hash tag.
/// Any Redis failure surfaces as <see cref="QueueUnavailableException"/>: callers fail closed.
/// </summary>
public sealed class RedisWaitingRoom(
    IConnectionMultiplexer redis,
    RedisKeys keys,
    IOptions<QueueOptions> options,
    IAdmissionTokenFactory tokens,
    TimeProvider time) : IWaitingRoom
{
    public async Task<JoinResult> JoinAsync(SaleWindow sale, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var result = await GuardAsync(async () => (RedisResult[])(await LuaScripts.RunAsync(
            redis.GetDatabase(),
            "queue_join",
            [keys.QueueState(sale.PhaseId), keys.QueuePre(sale.PhaseId), keys.QueueLine(sale.PhaseId), keys.QueueSeq(sale.PhaseId), keys.QueueDone(sale.PhaseId), keys.QueueOpening(sale.PhaseId)],
            [InitialStatus(sale, now).ToString(), sale.OpensAt.ToUnixTimeMilliseconds(), sale.ClosesAt.ToUnixTimeMilliseconds(), userId.ToString()]))!);

        var number = (long)result[1];
        return new JoinResult(ParseStatus((string)result[0]!), number > 0 ? number : null);
    }

    public async Task<ClaimResult> StatusAsync(SaleWindow sale, Guid userId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var window = TimeSpan.FromMinutes(options.Value.ShoppingWindowMinutes);
        var db = redis.GetDatabase();
        var r = await GuardAsync(async () => (RedisResult[])(await LuaScripts.RunAsync(
            db,
            "queue_status_claim",
            [
                keys.QueueState(sale.PhaseId), keys.QueueLine(sale.PhaseId), keys.QueueAdmitted(sale.PhaseId),
                keys.QueueActive(sale.PhaseId), keys.QueueTokens(sale.PhaseId), keys.QueueDone(sale.PhaseId),
                keys.QueueSeq(sale.PhaseId), keys.QueuePre(sale.PhaseId), keys.QueueOpening(sale.PhaseId),
            ],
            [
                InitialStatus(sale, now).ToString(), sale.OpensAt.ToUnixTimeMilliseconds(), sale.ClosesAt.ToUnixTimeMilliseconds(),
                userId.ToString(), now.ToUnixTimeMilliseconds(), (long)window.TotalMilliseconds,
                options.Value.MaxActiveShoppers, sale.QueueEnabled ? "0" : "1",
            ]))!);

        var number = (long)r[1];
        var active = (long)r[6] == 1;
        var untilMs = (long)r[9];
        DateTimeOffset? activeUntil = active ? DateTimeOffset.FromUnixTimeMilliseconds(untilMs) : null;
        var token = (string?)r[10];
        if (active && string.IsNullOrEmpty(token))
        {
            token = await EnsureTokenAsync(db, sale, userId, activeUntil!.Value);
        }

        return new ClaimResult(
            ParseStatus((string)r[0]!),
            number > 0 ? number : null,
            (long)r[2],
            (long)r[3],
            (long)r[4],
            (long)r[5] == 1,
            active,
            (long)r[7] == 1,
            (long)r[8] == 1,
            activeUntil,
            active ? token : null);
    }

    public Task LeaveAsync(SaleWindow sale, Guid userId, CancellationToken ct) =>
        GuardAsync(async () => await LuaScripts.RunAsync(
            redis.GetDatabase(),
            "queue_leave",
            [
                keys.QueuePre(sale.PhaseId), keys.QueueLine(sale.PhaseId), keys.QueueActive(sale.PhaseId), keys.QueueTokens(sale.PhaseId),
                keys.QueueDone(sale.PhaseId), keys.QueueOpening(sale.PhaseId), keys.QueueState(sale.PhaseId),
            ],
            [userId.ToString()]));

    public Task<bool> IsMemberAsync(SaleWindow sale, Guid userId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var db = redis.GetDatabase();
            var id = (RedisValue)userId.ToString();
            if (await db.SetContainsAsync(keys.QueueDone(sale.PhaseId), id))
            {
                return false;
            }

            return await db.HashExistsAsync(keys.QueueLine(sale.PhaseId), id)
                || await db.SetContainsAsync(keys.QueuePre(sale.PhaseId), id)
                || await db.SetContainsAsync(keys.QueueOpening(sale.PhaseId), id);
        });

    public Task<bool> IsDoneAsync(SaleWindow sale, Guid userId, CancellationToken ct) =>
        GuardAsync(async () => await redis.GetDatabase().SetContainsAsync(keys.QueueDone(sale.PhaseId), userId.ToString()));

    public Task<bool> IsActiveAsync(Guid phaseId, Guid userId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var score = await redis.GetDatabase().SortedSetScoreAsync(keys.QueueActive(phaseId), userId.ToString());
            return score is { } expiresAtMs && expiresAtMs > time.GetUtcNow().ToUnixTimeMilliseconds();
        });

    public Task<bool> IsCurrentTokenAsync(Guid phaseId, Guid userId, string token, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (!await IsActiveAsync(phaseId, userId, ct))
            {
                return false;
            }

            var stored = await redis.GetDatabase().HashGetAsync(keys.QueueTokens(phaseId), userId.ToString());
            return stored.HasValue && stored == token;
        });

    public Task<double> GetRateEmaAsync(Guid phaseId, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            var value = await redis.GetDatabase().HashGetAsync(keys.QueueState(phaseId), "rateEma");
            return value.HasValue && double.TryParse((string?)value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ? rate : 0d;
        });

    /// <summary>
    /// Status of a room that does not exist yet. A queued sale before its opening collects a pre-queue; anything else
    /// is Open at once (nobody could have joined earlier, so there is nothing to shuffle).
    /// </summary>
    internal static RoomStatus InitialStatus(SaleWindow sale, DateTimeOffset now)
    {
        if (now >= sale.ClosesAt)
        {
            return RoomStatus.Closed;
        }

        return sale.QueueEnabled && now < sale.OpensAt ? RoomStatus.Pre : RoomStatus.Open;
    }

    private async Task<string> EnsureTokenAsync(IDatabase db, SaleWindow sale, Guid userId, DateTimeOffset expiresAt)
    {
        var candidate = tokens.Create(userId, sale.EventId, sale.PhaseId, expiresAt);
        return await GuardAsync(async () =>
        {
            var tokensKey = keys.QueueTokens(sale.PhaseId);
            if (await db.HashSetAsync(tokensKey, userId.ToString(), candidate, When.NotExists))
            {
                // Like every queue key, tokens go away a day after the sale closes.
                var keepUntil = sale.ClosesAt.AddDays(1);
                var floor = time.GetUtcNow().AddHours(1);
                await db.KeyExpireAsync(tokensKey, (keepUntil > floor ? keepUntil : floor).UtcDateTime);
                return candidate;
            }

            // Another tab won the race: everyone must share its token.
            return (string?)await db.HashGetAsync(tokensKey, userId.ToString()) ?? candidate;
        });
    }

    private static RoomStatus ParseStatus(string value) => Enum.Parse<RoomStatus>(value);

    internal static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or ObjectDisposedException)
        {
            // RedisTimeoutException derives from TimeoutException, not RedisException: both mean "Redis is not answering".
            throw new QueueUnavailableException("The waiting room could not reach Redis.", ex);
        }
    }
}
