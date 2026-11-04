using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Realtime;

public static class RealtimeSetup
{
    /// <summary>
    /// SignalR with the Redis backplane, for the api (which maps <see cref="EventsHub"/>) and the worker (which only sends).
    /// The backplane opens its own connection: it disposes it on shutdown, which must not take the app's multiplexer
    /// down with it. Channels carry the environment prefix so environments sharing a Redis never see each other.
    /// </summary>
    public static IServiceCollection AddTicketRushSignalR(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(DependencyInjection.RedisConnectionName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{DependencyInjection.RedisConnectionName} is not configured.");
        var environment = configuration[$"{RedisOptions.SectionName}:{nameof(RedisOptions.Environment)}"] ?? new RedisOptions().Environment;

        services.AddSignalR().AddStackExchangeRedis(connectionString, options =>
        {
            options.Configuration.ChannelPrefix = RedisChannel.Literal($"tr:{environment}:signalr:");
            options.Configuration.AbortOnConnectFail = false;
        });
        return services;
    }
}
