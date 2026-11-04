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

    private sealed class FixedCatalog(HoldCatalogEntry entry) : IHoldCatalog
    {
        public Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct) => Task.FromResult<HoldCatalogEntry?>(entry);
    }

    private LiveInventoryEvents Live() =>
        new(new FixedEvents(new LiveInventoryEvent(_h.EventId, _h.GaTiers)), Options.Create(new QueueOptions()), _h.Time);

    private RedisDistributedLock Lock() => new(_h.Redis, _h.Keys, NullLogger<RedisDistributedLock>.Instance);

    private IServiceScopeFactory Scopes()
    {
        var entry = new HoldCatalogEntry(_h.EventId, "x", [], [new CatalogTier(_h.GaTier, "GA", 1, TicketKind.GeneralAdmission, _h.GaCapacity)], []);
        return new ServiceCollection()
            .AddSingleton<IHoldCatalog>(new FixedCatalog(entry))
            .AddSingleton<ISoldInventorySource, NoTicketsSoldInventorySource>()
            .AddSingleton<IInventoryStore>(_h.Store)
            .AddScoped<InventorySeeder>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
    }

    private InventoryReconcilerService Reconciler() =>
        new(Live(), _h.Store, Scopes(), Lock(), _h.Time, NullLogger<InventoryReconcilerService>.Instance);

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
}
