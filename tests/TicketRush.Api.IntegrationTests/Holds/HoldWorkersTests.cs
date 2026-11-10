extern alias WorkerAssembly;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Locking;
using WorkerAssembly::TicketRush.Worker.Inventory;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Inventory workers of spec 07, one pass at a time against a real Redis.</summary>
public sealed class HoldWorkersTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private InventoryHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await InventoryHarness.CreateAsync(containers, gaCapacity: 10);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private sealed class FixedEvents(params LiveInventoryEvent[] events) : IInventoryEventSource
    {
        public Task<IReadOnlyList<LiveInventoryEvent>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LiveInventoryEvent>>(events);
    }

    /// <summary>Stands in for the tickets table (spec 08): the seats the database records as sold.</summary>
    private sealed class DatabaseSold(params int[] seats) : ISoldInventorySource
    {
        public Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct) =>
            Task.FromResult(new SoldInventory(seats, new Dictionary<Guid, int>(), new Dictionary<Guid, int>()));
    }

    private sealed class FixedCatalog(HoldCatalogEntry entry) : IHoldCatalog
    {
        public Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct) => Task.FromResult<HoldCatalogEntry?>(entry);
    }

    private LiveInventoryEvents Live() =>
        new(new FixedEvents(new LiveInventoryEvent(_h.EventId, _h.GaTiers)), Options.Create(new QueueOptions()), _h.Time);

    private RedisDistributedLock Lock() => new(_h.Redis, _h.Keys, NullLogger<RedisDistributedLock>.Instance);

    private IServiceScopeFactory Scopes(ISoldInventorySource? sold = null)
    {
        var entry = new HoldCatalogEntry(_h.EventId, "x", [], [new CatalogTier(_h.GaTier, "GA", 1, TicketKind.GeneralAdmission, _h.GaCapacity)], []);
        return new ServiceCollection()
            .AddSingleton<IHoldCatalog>(new FixedCatalog(entry))
            .AddSingleton(sold ?? new DatabaseSold())
            .AddSingleton<IInventoryStore>(_h.Store)
            .AddScoped<InventorySeeder>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private HoldExpiryService Sweeper(TimeProvider? time = null) =>
        new(new LiveInventoryEvents(new FixedEvents(new LiveInventoryEvent(_h.EventId, _h.GaTiers)), Options.Create(new QueueOptions()), time ?? _h.Time),
            _h.Store, Lock(), time ?? _h.Time, NullLogger<HoldExpiryService>.Instance);

    private InventoryReconcilerService Reconciler(ISoldInventorySource? sold = null) =>
        new(Live(), _h.Store, Scopes(sold), Lock(), _h.Time, NullLogger<InventoryReconcilerService>.Instance);

    [Fact]
    public async Task A_quick_pass_warms_cold_live_events()
    {
        await _h.Db.KeyDeleteAsync([_h.Keys.InvMeta(_h.EventId), _h.Keys.InvGa(_h.EventId, _h.GaTier)]);

        await Reconciler().RunOnceAsync(full: false, Ct);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct);
        snapshot.Warm.ShouldBeTrue();
        snapshot.GaRemaining[_h.GaTier].ShouldBe(10);
    }

    [Fact]
    public async Task A_quick_pass_leaves_warm_events_alone_and_a_full_pass_repairs_them()
    {
        var reconciler = Reconciler();
        await _h.Db.StringSetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier), 99);

        await reconciler.RunOnceAsync(full: false, Ct);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(99);

        await reconciler.RunOnceAsync(full: true, Ct);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier))).ShouldBe(10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_sold_set_lost_with_redis_state_is_rebuilt_from_the_database(bool fullPass)
    {
        // R3: AOF everysec can drop the last second of writes, or the whole dataset after a restart without AOF.
        var reconciler = Reconciler(new DatabaseSold(4, 5));
        await reconciler.RunOnceAsync(full: true, Ct);
        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct)).Sold.ShouldBe([4, 5]);
        await _h.Db.KeyDeleteAsync([_h.Keys.InvSold(_h.EventId), _h.Keys.InvMeta(_h.EventId)]);
        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct)).Sold.ShouldBeEmpty();

        await reconciler.RunOnceAsync(fullPass, Ct);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct);
        snapshot.Warm.ShouldBeTrue();
        snapshot.Sold.ShouldBe([4, 5]);
        (await _h.HoldAsync(Guid.NewGuid(), 4)).Status.ShouldBe(HoldStatus.SeatUnavailable);
    }

    [Fact]
    public async Task Due_holds_are_released_and_their_seats_published_as_freed()
    {
        var kept = await _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), [2], ttl: TimeSpan.FromMinutes(20)), Ct);
        await _h.HoldAsync(Guid.NewGuid(), 1);
        var messages = await _h.ListenAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(9));

        await Sweeper().RunOnceAsync(Ct);

        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct)).Held.ShouldBe([2]);
        (await _h.OwnerOfAsync(2)).ShouldBe(kept.HoldId!.Value.ToString("N"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (messages.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        messages.ShouldContain(m => m.Contains("\"freed\":[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Holds_expire_within_two_seconds_of_expiresAt()
    {
        using var sweeper = Sweeper(TimeProvider.System);
        await sweeper.StartAsync(Ct);
        try
        {
            var hold = await _h.Store.CreateHoldAsync(
                new HoldRequest(_h.EventId, Guid.NewGuid(), [9], [], _h.GaTiers, 8, false, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2)), Ct);

            while ((await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct)).Held.Contains(9) && DateTimeOffset.UtcNow < hold.ExpiresAt!.Value.AddSeconds(10))
            {
                await Task.Delay(50, Ct);
            }

            (DateTimeOffset.UtcNow - hold.ExpiresAt!.Value).ShouldBeLessThan(TimeSpan.FromSeconds(2));
            (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, Ct)).Held.ShouldNotContain(9);
        }
        finally
        {
            await sweeper.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Only_the_leader_sweeps()
    {
        using var first = Sweeper(TimeProvider.System);
        using var second = Sweeper(TimeProvider.System);
        await first.StartAsync(Ct);
        await second.StartAsync(Ct);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);

            new[] { first.TickCount, second.TickCount }.Count(t => t > 0).ShouldBe(1);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
        }
    }
}
