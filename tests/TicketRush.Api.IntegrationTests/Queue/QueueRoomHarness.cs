using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Queue;

/// <summary>Token factory stand-in for tests that only care about Redis behaviour: tokens are unique random strings.</summary>
public sealed class FakeTokenFactory : IAdmissionTokenFactory
{
    public string Create(Guid userId, Guid eventId, Guid phaseId, DateTimeOffset expiresAt) => $"tok-{Guid.NewGuid():N}";

    public AdmissionTokenClaims? Validate(string token) => null;
}

/// <summary>One isolated Redis namespace + fake clock + queue options, shared by the waiting-room test classes.</summary>
public sealed class QueueRoomHarness : IAsyncDisposable
{
    private QueueRoomHarness(ConnectionMultiplexer redis, RedisKeys keys, QueueOptions options, FakeTimeProvider time)
    {
        Redis = redis;
        Keys = keys;
        Options = options;
        Time = time;
        Room = new RedisWaitingRoom(redis, keys, Microsoft.Extensions.Options.Options.Create(options), new FakeTokenFactory(), time);
    }

    public ConnectionMultiplexer Redis { get; }

    public RedisKeys Keys { get; }

    public QueueOptions Options { get; }

    public FakeTimeProvider Time { get; }

    public RedisWaitingRoom Room { get; }

    public IDatabase Db => Redis.GetDatabase();

    public static async Task<QueueRoomHarness> CreateAsync(ContainersFixture containers, Action<QueueOptions>? configure = null, DateTimeOffset? start = null)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var keys = new RedisKeys(Microsoft.Extensions.Options.Options.Create(new RedisOptions { Environment = $"q{Guid.NewGuid():N}"[..12] }));
        var options = new QueueOptions();
        configure?.Invoke(options);
        var time = new FakeTimeProvider(start ?? DateTimeOffset.UtcNow);
        return new QueueRoomHarness(redis, keys, options, time);
    }

    /// <summary>A sale opening <paramref name="opensIn"/> from the fake now (negative: already open).</summary>
    public SaleWindow Sale(TimeSpan opensIn, TimeSpan? lasts = null, bool queueEnabled = true) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Time.GetUtcNow() + opensIn,
        Time.GetUtcNow() + opensIn + (lasts ?? TimeSpan.FromHours(3)),
        queueEnabled);

    public ValueTask DisposeAsync() => Redis.DisposeAsync();
}
