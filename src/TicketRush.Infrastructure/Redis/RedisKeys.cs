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
}
