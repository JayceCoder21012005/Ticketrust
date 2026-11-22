using System.Text.Json;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>
/// The MoMo session (payUrl, expiry) created for one idempotency key (spec 18). MoMo refuses a second create with the same
/// orderId and does not give the payUrl back, so a double click or a retry is answered from here. In Redis, so every api
/// instance shares it.
/// </summary>
public sealed class MoMoSessionCache(IConnectionMultiplexer redis, RedisKeys keys)
{
    private sealed record Entry(string SessionId, string RedirectUrl, DateTimeOffset ExpiresAt);

    public async Task<PaymentSession?> GetAsync(string idempotencyKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var value = await redis.GetDatabase().StringGetAsync(keys.PayMoMoSession(idempotencyKey));
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        var entry = JsonSerializer.Deserialize<Entry>(value.ToString())!;
        return new PaymentSession(entry.SessionId, new Uri(entry.RedirectUrl), entry.ExpiresAt);
    }

    public async Task SetAsync(string idempotencyKey, PaymentSession session, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(new Entry(session.SessionId, session.RedirectUrl.ToString(), session.ExpiresAt));
        await redis.GetDatabase().StringSetAsync(keys.PayMoMoSession(idempotencyKey), json, ttl);
    }
}
