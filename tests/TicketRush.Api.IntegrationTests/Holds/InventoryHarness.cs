using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>One isolated Redis namespace + fake clock + a warmed event, shared by the inventory test classes.</summary>
public sealed class InventoryHarness : IAsyncDisposable
{
    private InventoryHarness(ConnectionMultiplexer redis, RedisKeys keys, FakeTimeProvider time, Guid gaTier)
    {
        Redis = redis;
        Keys = keys;
        Time = time;
        GaTier = gaTier;
        Store = new RedisInventoryStore(redis, keys, time);
    }

    public ConnectionMultiplexer Redis { get; }

    public RedisKeys Keys { get; }

    public FakeTimeProvider Time { get; }

    public RedisInventoryStore Store { get; }

    public Guid EventId { get; } = Guid.NewGuid();

    /// <summary>One GA tier with capacity <see cref="GaCapacity"/>.</summary>
    public Guid GaTier { get; }

    public int GaCapacity { get; private set; }

    public IReadOnlyList<Guid> GaTiers => [GaTier];

    public IDatabase Db => Redis.GetDatabase();

    public static async Task<InventoryHarness> CreateAsync(ContainersFixture containers, int gaCapacity = 5)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var keys = new RedisKeys(Microsoft.Extensions.Options.Options.Create(new RedisOptions { Environment = $"i{Guid.NewGuid():N}"[..12] }));
        var harness = new InventoryHarness(redis, keys, new FakeTimeProvider(DateTimeOffset.UtcNow), Guid.NewGuid()) { GaCapacity = gaCapacity };
        await harness.Db.StringSetAsync(keys.InvGa(harness.EventId, harness.GaTier), gaCapacity);
        await harness.Db.HashSetAsync(keys.InvMeta(harness.EventId), "warm", 1);
        return harness;
    }

    public HoldRequest Request(Guid userId, int[]? seats = null, GaLine[]? ga = null, int maxTickets = 8, bool replace = false, TimeSpan? ttl = null) =>
        new(EventId, userId, seats ?? [], ga ?? [], GaTiers, maxTickets, replace, Time.GetUtcNow(), ttl ?? TimeSpan.FromMinutes(8));

    public Task<HoldOutcome> HoldAsync(Guid userId, params int[] seats) =>
        Store.CreateHoldAsync(Request(userId, seats), CancellationToken.None);

    public async Task<string?> OwnerOfAsync(int seat) => await Db.HashGetAsync(Keys.InvHeld(EventId), seat);

    /// <summary>Collects every delta published for the event from now on.</summary>
    public async Task<ConcurrentQueue<string>> ListenAsync()
    {
        var messages = new ConcurrentQueue<string>();
        await Redis.GetSubscriber().SubscribeAsync(Keys.InvChanges(EventId), (_, message) => messages.Enqueue(message!));
        return messages;
    }

    public ValueTask DisposeAsync() => Redis.DisposeAsync();
}
