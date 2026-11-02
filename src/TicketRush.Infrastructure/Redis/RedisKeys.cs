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
/// </list>
/// </summary>
public sealed class RedisKeys(IOptions<RedisOptions> options)
{
    public string Prefix { get; } = $"tr:{options.Value.Environment}:";

    public RedisKey Lock(string name) => $"{Prefix}lock:{name}";
}
