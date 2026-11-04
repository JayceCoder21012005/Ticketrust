extern alias WorkerAssembly;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;
using WorkerAssembly::TicketRush.Worker.Inventory;
using WorkerAssembly::TicketRush.Worker.Queue;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>The waiting room follows the inventory: Open ↔ AllHeld ↔ SoldOut (spec 06 listener, built with 07).</summary>
public sealed class QueueRoomStatusTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private InventoryHarness _h = null!;
    private SaleWindow _sale = null!;

    public async ValueTask InitializeAsync()
    {
        _h = await InventoryHarness.CreateAsync(containers, gaCapacity: 1);
        var now = _h.Time.GetUtcNow();
        _sale = new SaleWindow(_h.EventId, Guid.NewGuid(), now.AddMinutes(-5), now.AddHours(1), QueueEnabled: true);
        await _h.Store.ReconcileAsync(
            new InventorySeed(_h.EventId, 2, new Dictionary<Guid, int> { [_h.GaTier] = 1 }, [], new Dictionary<Guid, int>(), new Dictionary<Guid, int>()), Ct);
        await SetStatusAsync("Open");
    }

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private sealed class FixedPhases(params SaleWindow[] sales) : IQueuePhaseSource
    {
        public Task<IReadOnlyList<SaleWindow>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SaleWindow>>(sales);
    }

    private sealed class FixedEvents(params LiveInventoryEvent[] events) : IInventoryEventSource
    {
        public Task<IReadOnlyList<LiveInventoryEvent>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LiveInventoryEvent>>(events);
    }

    private QueueRoomStatusService Service() => new(
        new LiveSales(new FixedPhases(_sale), Options.Create(new QueueOptions()), _h.Time),
        new LiveInventoryEvents(new FixedEvents(new LiveInventoryEvent(_h.EventId, _h.GaTiers)), Options.Create(new QueueOptions()), _h.Time),
        _h.Store,
        _h.Redis,
        _h.Keys,
        new RedisDistributedLock(_h.Redis, _h.Keys, NullLogger<RedisDistributedLock>.Instance),
        _h.Time,
        NullLogger<QueueRoomStatusService>.Instance);

    private Task<bool> SetStatusAsync(string status) => _h.Db.HashSetAsync(_h.Keys.QueueState(_sale.PhaseId), "status", status);

    private async Task<string?> StatusAsync() => await _h.Db.HashGetAsync(_h.Keys.QueueState(_sale.PhaseId), "status");

    private Task<HoldOutcome> HoldEverythingAsync() =>
        _h.Store.CreateHoldAsync(_h.Request(Guid.NewGuid(), [0, 1], [new GaLine(_h.GaTier, 1)]), Ct);

    [Fact]
    public async Task Holding_every_seat_turns_the_room_AllHeld()
    {
        await HoldEverythingAsync();

        await Service().RunOnceAsync(Ct);

        (await StatusAsync()).ShouldBe("AllHeld");
    }

    [Fact]
    public async Task Selling_every_seat_turns_it_SoldOut()
    {
        var user = Guid.NewGuid();
        var hold = await _h.Store.CreateHoldAsync(_h.Request(user, [0, 1], [new GaLine(_h.GaTier, 1)]), Ct);
        await _h.Store.CommitAsync(_h.EventId, hold.HoldId!.Value, user, [0, 1], [new GaLine(_h.GaTier, 1)], _h.GaTiers, Ct);

        await Service().RunOnceAsync(Ct);

        (await StatusAsync()).ShouldBe("SoldOut");
    }

    [Fact]
    public async Task Releasing_a_seat_reopens_the_room()
    {
        var hold = await HoldEverythingAsync();
        var service = Service();
        await service.RunOnceAsync(Ct);

        await _h.Store.ReleaseHoldAsync(_h.EventId, hold.HoldId!.Value, _h.GaTiers, Ct);
        await service.RunOnceAsync(Ct);

        (await StatusAsync()).ShouldBe("Open");
    }

    [Theory]
    [InlineData("Pre")]
    [InlineData("Opening")]
    [InlineData("Closed")]
    public async Task Rooms_that_are_not_selling_are_never_touched(string status)
    {
        await SetStatusAsync(status);
        await HoldEverythingAsync();

        await Service().RunOnceAsync(Ct);

        (await StatusAsync()).ShouldBe(status);
    }

    [Fact]
    public async Task Cancelling_an_event_releases_its_holds_and_closes_its_rooms()
    {
        await using var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Include(e => e.Phases).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        var store = scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        var keys = scope.ServiceProvider.GetRequiredService<RedisKeys>();
        var db = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await store.CreateHoldAsync(new HoldRequest(evt.Id, Guid.NewGuid(), [5], [], [], 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8)), Ct);
        foreach (var phase in evt.Phases)
        {
            await db.HashSetAsync(keys.QueueState(phase.Id), "status", "Open");
        }

        await scope.ServiceProvider.GetRequiredService<EventCancelledInventoryHandler>()
            .HandleAsync(new EventCancelled(evt.Id, evt.OrganizerId), new IntegrationEventContext(Guid.NewGuid(), DateTimeOffset.UtcNow, 1), Ct);

        (await store.GetSnapshotAsync(evt.Id, [], Ct)).Held.ShouldBeEmpty();
        foreach (var phase in evt.Phases)
        {
            ((string?)await db.HashGetAsync(keys.QueueState(phase.Id), "status")).ShouldBe("Closed");
        }
    }
}
