using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TicketRush.Infrastructure.Redis;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>Environment segment of every key (<c>tr:{env}:</c>) so environments can share a server safely.</summary>
    [Required]
    [RegularExpression("^[a-z0-9-]{1,20}$")]
    public string Environment { get; set; } = "dev";
}

/// <summary>
/// Builds every Redis key. Owners document their keys in their spec:
/// <list type="bullet">
/// <item><c>tr:{env}:lock:{name}</c> — string, random owner token, PX = lock TTL (spec 01, <see cref="Locking.RedisDistributedLock"/>).</item>
/// <item><c>tr:{env}:q:{sale:&lt;phaseId&gt;}:state|pre|line|seq|admitted|active|tokens|done</c> — waiting room of one sale phase (spec 06). One hash tag, so Lua scripts stay Cluster-safe.</item>
/// <item><c>tr:{env}:inv:{evt:&lt;eventId&gt;}:sold|held|ga:&lt;tier&gt;|gaheld|hold:&lt;id&gt;|expiry|userhold:&lt;user&gt;|bought:&lt;user&gt;|ver|commits|committed:&lt;orderId&gt;|meta</c> and channel <c>…:changes</c> — inventory of one event (spec 07).</item>
/// <item><c>tr:{env}:pay:mock:session:{id}|idem:{sha256}|refund:{sha256}|deliveries</c> — sessions, idempotency markers and the webhook delivery queue of the mock payment gateway (spec 08).</item>
/// <item><c>tr:{env}:qr-key:{userId}</c> — string counter of one user's qr-key requests in the current 60 s window (spec 10, <see cref="Tickets.RedisQrKeyThrottle"/>).</item>
/// <item><c>tr:{env}:rl:{policy}:{hash}</c> — ZSET of one partition's accepted requests, scored in ms (spec 12, <see cref="RateLimiting.RedisSlidingWindowStore"/>).</item>
/// </list>
/// </summary>
public sealed class RedisKeys(IOptions<RedisOptions> options)
{
    public string Prefix { get; } = $"tr:{options.Value.Environment}:";

    public RedisKey Lock(string name) => $"{Prefix}lock:{name}";

    /// <summary>HASH: status, opensAt, closesAt, rateEma.</summary>
    public RedisKey QueueState(Guid phaseId) => Queue(phaseId, "state");

    /// <summary>SET: userIds that joined before opening (and, while Opening, people who arrived after T0).</summary>
    public RedisKey QueuePre(Guid phaseId) => Queue(phaseId, "pre");

    /// <summary>SET: the pre-queue frozen at T0 (renamed from <c>pre</c>), drained by the shuffle.</summary>
    public RedisKey QueueOpening(Guid phaseId) => Queue(phaseId, "opening");

    /// <summary>HASH: userId → ticket number.</summary>
    public RedisKey QueueLine(Guid phaseId) => Queue(phaseId, "line");

    /// <summary>STRING: last ticket number issued.</summary>
    public RedisKey QueueSeq(Guid phaseId) => Queue(phaseId, "seq");

    /// <summary>STRING: every number up to this value is admitted.</summary>
    public RedisKey QueueAdmitted(Guid phaseId) => Queue(phaseId, "admitted");

    /// <summary>ZSET: userId → admission expiry (unix ms).</summary>
    public RedisKey QueueActive(Guid phaseId) => Queue(phaseId, "active");

    /// <summary>HASH: userId → current admission token.</summary>
    public RedisKey QueueTokens(Guid phaseId) => Queue(phaseId, "tokens");

    /// <summary>SET: users who left or whose shopping window expired; they must rejoin at the tail.</summary>
    public RedisKey QueueDone(Guid phaseId) => Queue(phaseId, "done");

    private RedisKey Queue(Guid phaseId, string name) => $"{Prefix}q:{{sale:{phaseId:N}}}:{name}";

    // Inventory of one event (spec 07). One hash tag per event so every hold script stays Cluster-safe.

    /// <summary>SET: sold seat indexes.</summary>
    public RedisKey InvSold(Guid eventId) => Inv(eventId, "sold");

    /// <summary>HASH: seat index → holdId.</summary>
    public RedisKey InvHeld(Guid eventId) => Inv(eventId, "held");

    /// <summary>STRING: GA tickets of a tier still free (capacity − sold − held).</summary>
    public RedisKey InvGa(Guid eventId, Guid tierId) => Inv(eventId, $"ga:{tierId:N}");

    /// <summary>HASH: GA tierId → quantity currently held (lets the reconciler recompute <see cref="InvGa"/>).</summary>
    public RedisKey InvGaHeld(Guid eventId) => Inv(eventId, "gaheld");

    /// <summary>HASH: userId, seats (csv), ga (json), expiresAt (unix ms).</summary>
    public RedisKey InvHold(Guid eventId, Guid holdId) => Inv(eventId, $"hold:{holdId:N}");

    /// <summary>ZSET: holdId → expiresAt (unix ms).</summary>
    public RedisKey InvExpiry(Guid eventId) => Inv(eventId, "expiry");

    /// <summary>STRING: the user's active holdId (PX = hold TTL).</summary>
    public RedisKey InvUserHold(Guid eventId, Guid userId) => Inv(eventId, $"userhold:{userId:N}");

    /// <summary>STRING: tickets the user already bought for this event (per-user limit).</summary>
    public RedisKey InvBought(Guid eventId, Guid userId) => Inv(eventId, $"bought:{userId:N}");

    /// <summary>STRING: version of the event's inventory, bumped by every mutating script and sent with each delta.</summary>
    public RedisKey InvVersion(Guid eventId) => Inv(eventId, "ver");

    /// <summary>STRING: checkout commits so far; the reconciler's fence against a commit landing after its database read.</summary>
    public RedisKey InvCommits(Guid eventId) => Inv(eventId, "commits");

    /// <summary>STRING: marker that the order's commit was applied (SET NX, 7 days); makes <c>inventory_commit</c> idempotent per order.</summary>
    public RedisKey InvCommitted(Guid eventId, Guid orderId) => Inv(eventId, $"committed:{orderId:N}");

    /// <summary>HASH: warm (1 once seeded), seatCount, gaCap:&lt;tierId&gt;.</summary>
    public RedisKey InvMeta(Guid eventId) => Inv(eventId, "meta");

    /// <summary>PUB/SUB: seat deltas, published inside the scripts.</summary>
    public RedisChannel InvChanges(Guid eventId) => RedisChannel.Literal($"{Prefix}inv:{{evt:{eventId:N}}}:changes");

    /// <summary>Pattern matching <see cref="InvChanges"/> of every event.</summary>
    public RedisChannel InvChangesPattern => RedisChannel.Pattern($"{Prefix}inv:*:changes");

    /// <summary>STRING: pending marker or stored response of one Idempotency-Key of one user (TTL = the endpoint's window).</summary>
    public RedisKey Idempotency(Guid userId, Guid key) => $"{Prefix}idem:{userId:N}:{key:N}";

    // Mock payment gateway (spec 08). Redis, not memory, so the api (which creates and completes sessions) and the worker
    // (which delivers the webhooks) see the same state.

    /// <summary>HASH: orderId, eventId, label, amount, currency, returnUrl, expiresAt (unix ms), createdAt (unix ms), status, refunded. PX = expiry + margin; a succeeded session keeps a long retention for refunds.</summary>
    public RedisKey PayMockSession(string sessionId) => $"{Prefix}pay:mock:session:{sessionId}";

    /// <summary>STRING: sessionId created for one idempotency key (SET NX). The key is hashed: callers choose it.</summary>
    public RedisKey PayMockIdempotency(string idempotencyKey) => $"{Prefix}pay:mock:idem:{Hash(idempotencyKey)}";

    /// <summary>STRING: JSON of the refund made for one idempotency key (SET NX).</summary>
    public RedisKey PayMockRefund(string idempotencyKey) => $"{Prefix}pay:mock:refund:{Hash(idempotencyKey)}";

    /// <summary>ZSET: JSON delivery (id, body, attempt) → due time (unix ms). A claimed delivery is pushed out by a lease until it is acknowledged.</summary>
    public RedisKey PayMockDeliveries => $"{Prefix}pay:mock:deliveries";

    /// <summary>STRING: qr-key requests made by one user in the current fixed window (PX = the window, set by the first request).</summary>
    public RedisKey QrKeyThrottle(Guid userId) => $"{Prefix}qr-key:{userId:N}";

    /// <summary>ZSET: the accepted requests of one partition under one rate-limit policy (spec 12). The partition (an ip, a user id, an email) is hashed, so no personal data sits in a key.</summary>
    public RedisKey RateLimit(string policy, string partition) =>
        $"{Prefix}rl:{policy}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(partition)))[..32].ToLowerInvariant()}";

    /// <summary>STRING: scans made by one staff member in the current 1 s window (spec 11, <see cref="Gates.RedisScanGuard"/>).</summary>
    public RedisKey ScanRate(Guid staffUserId) => $"{Prefix}scan-rate:{staffUserId:N}";

    /// <summary>STRING: JSON of the answer to one payload (hashed by the caller) scanned by one staff member in the last 3 s.</summary>
    public RedisKey ScanRepeat(Guid staffUserId, string payloadHash) => $"{Prefix}scan-repeat:{staffUserId:N}:{payloadHash}";

    private static string Hash(string value) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private RedisKey Inv(Guid eventId, string name) => $"{Prefix}inv:{{evt:{eventId:N}}}:{name}";
}
