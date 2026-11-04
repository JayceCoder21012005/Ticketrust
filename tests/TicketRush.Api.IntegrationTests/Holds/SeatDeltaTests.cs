extern alias WorkerAssembly;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Realtime;
using TicketRush.Infrastructure.Redis;
using WorkerAssembly::TicketRush.Worker.Inventory;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>
/// Script delta → Redis pub/sub → broadcaster in a separate host → Redis backplane → hub in the api → browser client
/// (spec 07: seats change on other clients within 1 s).
/// </summary>
public sealed class SeatDeltaTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ApiFactory _factory = null!;
    private IHost _workerHost = null!;
    private ConnectionMultiplexer _redis = null!;
    private RedisInventoryStore _store = null!;
    private SeatDeltaBroadcaster _broadcaster = null!;
    private string _token = null!;
    private readonly Guid _event = Guid.NewGuid();

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        using var client = AuthApi.CreateClient(_factory);
        _token = (await new AuthApi(_factory, client).SignInWithRoleAsync($"viewer-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;

        // A second, independent host: its hub context reaches the api's clients only through the Redis backplane.
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For("unused", containers.RedisConnectionString));
        builder.Services.AddTicketRushSignalR(builder.Configuration);
        _workerHost = builder.Build();

        _redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var keys = new RedisKeys(Options.Create(new RedisOptions { Environment = "test" }));
        _store = new RedisInventoryStore(_redis, keys, TimeProvider.System);
        _broadcaster = new SeatDeltaBroadcaster(
            _redis, keys, _workerHost.Services.GetRequiredService<IHubContext<EventsHub>>(),
            new RedisDistributedLock(_redis, keys, NullLogger<RedisDistributedLock>.Instance), TimeProvider.System, NullLogger<SeatDeltaBroadcaster>.Instance);
        await _broadcaster.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _broadcaster.StopAsync(CancellationToken.None);
        _broadcaster.Dispose();
        _workerHost.Dispose();
        await _redis.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private HubConnection Connect(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/events"), options =>
        {
            options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            options.Transports = HttpTransportType.LongPolling;
            options.AccessTokenProvider = () => Task.FromResult(token);
        })
        .Build();

    private async Task<(HubConnection Connection, BlockingCollection<SeatDelta> Deltas)> JoinAsync()
    {
        var connection = Connect(_token);
        var deltas = new BlockingCollection<SeatDelta>();
        connection.On<SeatDelta>("SeatDelta", deltas.Add);
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("JoinEvent", _event, Ct);
        return (connection, deltas);
    }

    private HoldRequest Request(params int[] seats) =>
        new(_event, Guid.NewGuid(), seats, [], [], 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));

    private static SeatDelta Next(BlockingCollection<SeatDelta> deltas, Func<SeatDelta, bool> match, TimeSpan within)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < within)
        {
            if (deltas.TryTake(out var delta, within - deadline.Elapsed) && match(delta))
            {
                return delta;
            }
        }

        throw new ShouldAssertException($"No matching SeatDelta within {within}.");
    }

    [Fact]
    public async Task A_hold_reaches_a_joined_client_within_one_second()
    {
        var (connection, deltas) = await JoinAsync();
        await using var _ = connection;
        await Task.Delay(300, Ct); // let the broadcaster win its leader lock

        var clock = Stopwatch.StartNew();
        await _store.CreateHoldAsync(Request(1, 2), Ct);

        var delta = Next(deltas, d => d.Held.Contains(1), TimeSpan.FromSeconds(1));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        delta.Held.ShouldBe([1, 2]);
        delta.V.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_released_hold_shows_as_freed_on_other_clients_within_one_second()
    {
        var (connection, deltas) = await JoinAsync();
        await using var _ = connection;
        await Task.Delay(300, Ct);
        var hold = await _store.CreateHoldAsync(Request(7), Ct);
        Next(deltas, d => d.Held.Contains(7), TimeSpan.FromSeconds(2));

        var clock = Stopwatch.StartNew();
        await _store.ReleaseHoldAsync(_event, hold.HoldId!.Value, [], Ct);

        Next(deltas, d => d.Freed.Contains(7), TimeSpan.FromSeconds(1));
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Clients_of_other_events_get_nothing()
    {
        var connection = Connect(_token);
        await using var _ = connection;
        var deltas = new BlockingCollection<SeatDelta>();
        connection.On<SeatDelta>("SeatDelta", deltas.Add);
        await connection.StartAsync(Ct);
        await connection.InvokeAsync("JoinEvent", Guid.NewGuid(), Ct);
        await Task.Delay(300, Ct);

        await _store.CreateHoldAsync(Request(3), Ct);

        deltas.TryTake(out var _, TimeSpan.FromSeconds(1)).ShouldBeFalse();
    }

    [Fact]
    public async Task Unauthenticated_connections_are_rejected()
    {
        await using var connection = Connect(null);

        await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync(Ct));
    }
}
