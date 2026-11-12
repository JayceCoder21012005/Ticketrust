using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Realtime;

public static class RealtimeSetup
{
    /// <summary>
    /// SignalR with the Redis backplane, for the api (which maps <see cref="EventsHub"/> and <see cref="UserHub"/>) and the worker (which only sends).
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

        // The only IUserNotifier registration: a host that forgets AddTicketRushSignalR fails to resolve it instead of silently
        // pushing nowhere, and no registration order can make a stand-in win.
        services.AddSingleton<IUserNotifier, UserNotifier>();
        return services;
    }
}
