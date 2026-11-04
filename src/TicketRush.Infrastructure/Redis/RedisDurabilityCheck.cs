using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace TicketRush.Infrastructure.Redis;

/// <summary>What the Redis server says about its append-only file.</summary>
public interface IRedisPersistenceProbe
{
    /// <returns>Whether AOF is on, or null when the server does not say.</returns>
    Task<bool?> IsAofEnabledAsync(CancellationToken ct);
}

/// <summary>
/// Reads <c>aof_enabled</c> from <c>INFO persistence</c>, the runtime view of <c>appendonly yes</c>. StackExchange.Redis
/// refuses both <c>CONFIG</c> and <c>INFO</c> unless the connection has <c>allowAdmin=true</c>, which production does not
/// (it would also unlock FLUSHALL), so INFO is called from a one-line script instead.
/// </summary>
public sealed class RedisPersistenceProbe(IConnectionMultiplexer redis) : IRedisPersistenceProbe
{
    private const string InfoScript = "return redis.call('INFO', 'persistence')";

    public async Task<bool?> IsAofEnabledAsync(CancellationToken ct)
    {
        var info = (string?)await redis.GetDatabase().ScriptEvaluateAsync(InfoScript).WaitAsync(ct);
        var line = info?.Split('\n').FirstOrDefault(l => l.StartsWith("aof_enabled:", StringComparison.Ordinal));
        return line is null ? null : line.Trim().EndsWith(":1", StringComparison.Ordinal);
    }
}

/// <summary>
/// Spec 07, R3: holds and the sold set live in Redis, so production Redis runs with <c>--appendonly yes</c> (fsync every
/// second, at most 1 s of writes lost on a crash). Without AOF a restart loses every hold. Checked once at start and only
/// outside Development; a probe that fails or is denied is logged at Information and never stops the host.
/// </summary>
public sealed partial class RedisDurabilityCheck(
    IRedisPersistenceProbe probe,
    IHostEnvironment environment,
    ILogger<RedisDurabilityCheck> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => CheckAsync(stoppingToken);

    public async Task CheckAsync(CancellationToken ct)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        bool? aof;
        try
        {
            aof = await probe.IsAofEnabledAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUnknown(logger, ex.Message);
            return;
        }

        switch (aof)
        {
            case false:
                LogAofOff(logger);
                break;
            case true:
                LogAofOn(logger);
                break;
            default:
                LogUnknown(logger, "INFO persistence has no aof_enabled field");
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis AOF persistence is off (appendonly is not yes): a Redis restart or crash loses every hold and the sold set until the reconciler rebuilds it. Start Redis with --appendonly yes (see README, Độ bền của Redis).")]
    private static partial void LogAofOff(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis AOF is on (appendfsync everysec by default): a crash can lose at most about 1 s of holds.")]
    private static partial void LogAofOn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read Redis persistence settings ({Reason}); skipping the AOF check.")]
    private static partial void LogUnknown(ILogger logger, string reason);
}
