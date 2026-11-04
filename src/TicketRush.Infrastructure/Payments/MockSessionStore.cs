using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Payments;
using Order = TicketRush.Domain.Orders.Order;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Redis persistence of the mock gateway (keys in <see cref="RedisKeys"/>): one hash per session, idempotency markers for
/// session creation and refunds. In Redis rather than in memory so the api (create, complete) and the worker (deliver) agree.
/// </summary>
public sealed class MockSessionStore(
    IConnectionMultiplexer redis,
    RedisKeys keys,
    MockWebhookQueue queue,
    IOptionsMonitor<MockGatewayOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredRefund(string RefundId, long Amount);

    /// <summary>
    /// Creates the session, or returns the one already made for <paramref name="idempotencyKey"/>. The hash is written
    /// first and the marker second (SET NX), so whoever sees a marker can always read its session.
    /// </summary>
    public async Task<MockSession> CreateAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, DateTimeOffset now, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var session = NewSession(order, returnUrl, expiresAt, now);
        var ttl = expiresAt - now + options.CurrentValue.SessionMargin;
        await WriteAsync(session, ttl).WaitAsync(ct);

        if (await db.StringSetAsync(keys.PayMockIdempotency(idempotencyKey), session.SessionId, ttl, When.NotExists).WaitAsync(ct))
        {
            return session;
        }

        await db.KeyDeleteAsync(keys.PayMockSession(session.SessionId)).WaitAsync(ct);
        var existingId = (string?)await db.StringGetAsync(keys.PayMockIdempotency(idempotencyKey)).WaitAsync(ct);
        var existing = existingId is null ? null : await GetAsync(existingId, ct);
        return existing is null || existing.OrderId != order.Id
            ? throw new PaymentRejectedException("The idempotency key belongs to another payment.")
            : existing;
    }

    public async Task<MockSession?> GetAsync(string sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = await redis.GetDatabase().HashGetAllAsync(keys.PayMockSession(sessionId)).WaitAsync(ct);
        return entries.Length == 0 ? null : Parse(sessionId, entries.ToDictionary(e => (string)e.Name!, e => (string)e.Value!));
    }

    /// <summary>
    /// Moves a pending session to <paramref name="outcome"/> and queues its webhooks in one transaction.
    /// Returns <c>false</c> when the session was no longer pending (a concurrent completion won).
    /// </summary>
    public async Task<bool> CompleteAsync(
        MockSession session,
        MockSessionStatus outcome,
        IReadOnlyList<MockDelivery> deliveries,
        DateTimeOffset due,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = keys.PayMockSession(session.SessionId);
        var transaction = redis.GetDatabase().CreateTransaction();
        transaction.AddCondition(Condition.HashEqual(key, "status", nameof(MockSessionStatus.Pending)));
        _ = transaction.HashSetAsync(key, [new("status", outcome.ToString()), new("completedAt", now.ToUnixTimeMilliseconds())]);
        if (outcome == MockSessionStatus.Succeeded)
        {
            _ = transaction.KeyExpireAsync(key, options.CurrentValue.SucceededRetention);
        }

        foreach (var delivery in deliveries)
        {
            queue.StageEnqueue(transaction, delivery, due);
        }

        return await transaction.ExecuteAsync().WaitAsync(ct);
    }

    /// <summary>The refund already made for this key, if any.</summary>
    public async Task<RefundResult?> GetRefundAsync(string idempotencyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var stored = (string?)await redis.GetDatabase().StringGetAsync(keys.PayMockRefund(idempotencyKey)).WaitAsync(ct);
        return stored is null ? null : ToResult(JsonSerializer.Deserialize<StoredRefund>(stored, Json)!);
    }

    /// <summary>Records a refund. The first caller per key wins and books the amount; later callers get the winner's result.</summary>
    public async Task<RefundResult> RecordRefundAsync(string sessionId, string idempotencyKey, long amount, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var db = redis.GetDatabase();
        var refund = new StoredRefund($"re_mock_{Guid.NewGuid():N}", amount);
        var won = await db.StringSetAsync(
            keys.PayMockRefund(idempotencyKey), JsonSerializer.Serialize(refund, Json), options.CurrentValue.SucceededRetention, When.NotExists).WaitAsync(ct);
        if (!won)
        {
            return await GetRefundAsync(idempotencyKey, ct) ?? ToResult(refund);
        }

        await db.HashIncrementAsync(keys.PayMockSession(sessionId), "refunded", amount).WaitAsync(ct);
        return ToResult(refund);
    }

    private static RefundResult ToResult(StoredRefund refund) => new(refund.RefundId, RefundStatus.Succeeded);

    private Task<bool> WriteAsync(MockSession session, TimeSpan ttl)
    {
        var key = keys.PayMockSession(session.SessionId);
        var transaction = redis.GetDatabase().CreateTransaction();
        _ = transaction.HashSetAsync(key,
        [
            new("orderId", session.OrderId.ToString("N")),
            new("eventId", session.EventId.ToString("N")),
            new("label", session.Label),
            new("amount", session.Amount.Amount),
            new("currency", session.Amount.Currency),
            new("returnUrl", session.ReturnUrl.AbsoluteUri),
            new("expiresAt", session.ExpiresAt.ToUnixTimeMilliseconds()),
            new("createdAt", session.CreatedAt.ToUnixTimeMilliseconds()),
            new("status", nameof(MockSessionStatus.Pending)),
            new("refunded", 0),
        ]);
        _ = transaction.KeyExpireAsync(key, ttl);
        return transaction.ExecuteAsync();
    }

    private static MockSession NewSession(Order order, Uri returnUrl, DateTimeOffset expiresAt, DateTimeOffset now) => new(
        $"mock_{Guid.NewGuid():N}",
        order.Id,
        order.EventId,
        $"Đơn hàng {order.Id.ToString("N")[..8].ToUpperInvariant()}",
        order.Total,
        returnUrl,
        expiresAt,
        now,
        MockSessionStatus.Pending,
        0);

    private static MockSession Parse(string sessionId, Dictionary<string, string> fields) => new(
        sessionId,
        Guid.ParseExact(fields["orderId"], "N"),
        Guid.ParseExact(fields["eventId"], "N"),
        fields["label"],
        new Money(long.Parse(fields["amount"], CultureInfo.InvariantCulture), fields["currency"]),
        new Uri(fields["returnUrl"], UriKind.Absolute),
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields["expiresAt"], CultureInfo.InvariantCulture)),
        DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(fields["createdAt"], CultureInfo.InvariantCulture)),
        Enum.Parse<MockSessionStatus>(fields["status"]),
        long.Parse(fields["refunded"], CultureInfo.InvariantCulture));
}
