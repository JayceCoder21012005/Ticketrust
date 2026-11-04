using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Warmer and reconciler: Redis is a mirror, the database is the truth (spec 07).</summary>
public sealed class InventoryReconcileTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly CancellationToken None = CancellationToken.None;
    private InventoryHarness _h = null!;

    public async ValueTask InitializeAsync() => _h = await InventoryHarness.CreateAsync(containers, gaCapacity: 10);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private InventorySeed Seed(int[]? sold = null, int gaSold = 0, Dictionary<Guid, int>? bought = null) => new(
        _h.EventId,
        SeatCount: 100,
        new Dictionary<Guid, int> { [_h.GaTier] = _h.GaCapacity },
        sold ?? [],
        new Dictionary<Guid, int> { [_h.GaTier] = gaSold },
        bought ?? []);

    private sealed class FakeCatalog(HoldCatalogEntry entry) : IHoldCatalog
    {
        public Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct) =>
            Task.FromResult<HoldCatalogEntry?>(eventId == entry.EventId ? entry : null);
    }

    private sealed class FakeSold(params int[] seats) : ISoldInventorySource
    {
        public Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct) =>
            Task.FromResult(new SoldInventory(seats, new Dictionary<Guid, int>(), new Dictionary<Guid, int>()));
    }

    [Fact]
    public async Task Warming_a_cold_event_sets_ga_to_capacity_and_marks_it_warm()
    {
        await _h.Db.KeyDeleteAsync([_h.Keys.InvMeta(_h.EventId), _h.Keys.InvGa(_h.EventId, _h.GaTier)]);

        var report = await _h.Store.ReconcileAsync(Seed(), None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.Warm.ShouldBeTrue();
        snapshot.GaRemaining[_h.GaTier].ShouldBe(10);
        report.GaFixed[_h.GaTier].ShouldBe(new GaFix(0, 10));
        ((int)await _h.Db.HashGetAsync(_h.Keys.InvMeta(_h.EventId), "seatCount")).ShouldBe(100);
    }

    [Fact]
    public async Task A_deleted_sold_set_is_restored_from_the_database_source()
    {
        var catalog = new HoldCatalogEntry(_h.EventId, "x", [], [new CatalogTier(_h.GaTier, "GA", 1, TicketKind.GeneralAdmission, 10)], []);
        var seeder = new InventorySeeder(new FakeCatalog(catalog), new FakeSold(1, 2), _h.Store);
        await seeder.SeedAsync(_h.EventId, None);
        await _h.Db.KeyDeleteAsync(_h.Keys.InvSold(_h.EventId));

        var report = (await seeder.SeedAsync(_h.EventId, None)).ShouldNotBeNull();

        report.SoldAdded.ShouldBe(2);
        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None)).Sold.ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Seeding_an_unknown_event_does_nothing()
    {
        var seeder = new InventorySeeder(new FakeCatalog(new HoldCatalogEntry(Guid.NewGuid(), "x", [], [], [])), new FakeSold(), _h.Store);

        (await seeder.SeedAsync(_h.EventId, None)).ShouldBeNull();
    }

    [Fact]
    public async Task Ga_drift_is_corrected_from_capacity_sold_and_held()
    {
        await _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), ga: [new GaLine(_h.GaTier, 3)]), None);
        await _h.Db.StringSetAsync(_h.Keys.InvGa(_h.EventId, _h.GaTier), 99);

        var report = await _h.Store.ReconcileAsync(Seed(gaSold: 2), None);

        report.GaFixed[_h.GaTier].ShouldBe(new GaFix(99, 5));
        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None)).GaRemaining[_h.GaTier].ShouldBe(5);
    }

    [Fact]
    public async Task Sold_seats_missing_from_the_database_are_removed()
    {
        await _h.Db.SetAddAsync(_h.Keys.InvSold(_h.EventId), [1, 7]);

        var report = await _h.Store.ReconcileAsync(Seed(sold: [1]), None);

        report.SoldRemoved.ShouldBe(1);
        (await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None)).Sold.ShouldBe([1]);
    }

    [Fact]
    public async Task Bought_counts_never_go_below_what_redis_already_counted()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _h.Db.StringSetAsync(_h.Keys.InvBought(_h.EventId, b), 5);

        await _h.Store.ReconcileAsync(Seed(bought: new() { [a] = 2, [b] = 3 }), None);

        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, a))).ShouldBe(2);
        ((int)await _h.Db.StringGetAsync(_h.Keys.InvBought(_h.EventId, b))).ShouldBe(5);
    }

    [Fact]
    public async Task Reconcile_without_drift_publishes_nothing()
    {
        await _h.Store.ReconcileAsync(Seed(sold: [3]), None);
        var version = (long)await _h.Db.StringGetAsync(_h.Keys.InvVersion(_h.EventId));
        var messages = await _h.ListenAsync();

        var report = await _h.Store.ReconcileAsync(Seed(sold: [3]), None);

        report.HadDrift.ShouldBeFalse();
        ((long)await _h.Db.StringGetAsync(_h.Keys.InvVersion(_h.EventId))).ShouldBe(version);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Publishing_an_event_warms_its_inventory()
    {
        await using var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(None);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.SingleAsync(e => e.Slug == "son-tung-mtp-live", None);
        var store = scope.ServiceProvider.GetRequiredService<IInventoryStore>();

        await scope.ServiceProvider.GetRequiredService<InventoryWarmHandler>()
            .HandleAsync(new EventPublished(evt.Id, evt.OrganizerId), new IntegrationEventContext(Guid.NewGuid(), DateTimeOffset.UtcNow, 1), None);

        (await store.GetSnapshotAsync(evt.Id, [], None)).Warm.ShouldBeTrue();
    }

    /// <summary>Plays the database while a checkout commits between its read and the reconcile script.</summary>
    private sealed class CommitDuringRead(InventoryHarness h, Guid user, Guid holdId) : ISoldInventorySource
    {
        private int _reads;

        public async Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                // The database answer below was read before this commit, so it does not contain it.
                await h.Store.CommitAsync(eventId, holdId, user, [1], [new GaLine(h.GaTier, 2)], h.GaTiers, ct);
                return new SoldInventory([], new Dictionary<Guid, int>(), new Dictionary<Guid, int>());
            }

            return new SoldInventory([1], new Dictionary<Guid, int> { [h.GaTier] = 2 }, new Dictionary<Guid, int> { [user] = 3 });
        }
    }

    [Fact]
    public async Task A_commit_landing_between_the_database_read_and_the_script_is_not_undone()
    {
        var user = Guid.NewGuid();
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [1], [new GaLine(_h.GaTier, 2)]), None);
        var catalog = new HoldCatalogEntry(_h.EventId, "x", [new CatalogSeat(1, "A", "1", "1", 0, 0, Guid.NewGuid())], [new CatalogTier(_h.GaTier, "GA", 1, TicketKind.GeneralAdmission, 10)], []);
        var seeder = new InventorySeeder(new FakeCatalog(catalog), new CommitDuringRead(_h, user, hold.HoldId!.Value), _h.Store);

        await seeder.SeedAsync(_h.EventId, None);

        var snapshot = await _h.Store.GetSnapshotAsync(_h.EventId, _h.GaTiers, None);
        snapshot.GaRemaining[_h.GaTier].ShouldBe(8);
        snapshot.Sold.ShouldBe([1]);
    }
}
