using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>
/// Cancelling an event stops selling before it cleans up (spec 07 follow-up A2): the rooms close and admissions are
/// revoked first, so no hold can be created while the holds are being released; a half-done run is simply run again.
/// </summary>
public sealed class EventCancelledInventoryHandlerTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ApiFactory _factory = null!;
    private AsyncServiceScope _scope;
    private Event _event = null!;
    private RedisKeys _keys = null!;
    private IDatabase _redis = null!;
    private IInventoryStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _scope = _factory.Services.CreateAsyncScope();
        await _scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        _event = await _scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Include(e => e.Phases).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _keys = _scope.ServiceProvider.GetRequiredService<RedisKeys>();
        _redis = _scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        _store = _scope.ServiceProvider.GetRequiredService<IInventoryStore>();
        foreach (var phase in _event.Phases)
        {
            await _redis.HashSetAsync(_keys.QueueState(phase.Id), "status", "Open");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private EventCancelledInventoryHandler Handler(IInventoryStore store) => new(
        _scope.ServiceProvider.GetRequiredService<IAppDbContext>(),
        store,
        _scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>(),
        _keys);

    private Task HandleAsync(IInventoryStore store) =>
        Handler(store).HandleAsync(new EventCancelled(_event.Id, _event.OrganizerId), new IntegrationEventContext(Guid.NewGuid(), DateTimeOffset.UtcNow, 1), Ct);

    private async Task<Guid> HoldSeatAsync(int seat)
    {
        var outcome = await _store.CreateHoldAsync(new HoldRequest(_event.Id, Guid.NewGuid(), [seat], [], [], 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8)), Ct);
        return outcome.HoldId!.Value;
    }

    private async Task<string> RoomStatusAsync(Guid phaseId) => (string?)await _redis.HashGetAsync(_keys.QueueState(phaseId), "status") ?? string.Empty;

    [Fact]
    public async Task Every_room_is_closed_before_the_first_hold_is_released()
    {
        await HoldSeatAsync(5);
        await HoldSeatAsync(6);
        var spy = new SpyStore(_store, async () =>
        {
            foreach (var phase in _event.Phases)
            {
                (await RoomStatusAsync(phase.Id)).ShouldBe("Closed");
            }
        });

        await HandleAsync(spy);

        spy.Released.Count.ShouldBe(2);
        spy.Checks.ShouldBe(2);
    }

    [Fact]
    public async Task Cancelling_revokes_the_admission_that_a_buy_request_would_present()
    {
        var phase = _event.Phases[0];
        var user = Guid.NewGuid();
        await _redis.SortedSetAddAsync(_keys.QueueActive(phase.Id), user.ToString(), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds());
        await _redis.HashSetAsync(_keys.QueueTokens(phase.Id), user.ToString(), "token-1");
        var room = _scope.ServiceProvider.GetRequiredService<IWaitingRoom>();
        (await room.IsCurrentTokenAsync(phase.Id, user, "token-1", Ct)).ShouldBeTrue();

        await HandleAsync(_store);

        (await room.IsCurrentTokenAsync(phase.Id, user, "token-1", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Holds_are_released_in_bounded_batches()
    {
        var holds = new List<Guid>();
        for (var seat = 5; seat < 9; seat++)
        {
            holds.Add(await HoldSeatAsync(seat));
        }

        var spy = new SpyStore(_store);
        await HandleAsync(spy);

        spy.Released.OrderBy(id => id).ShouldBe(holds.OrderBy(id => id));
        spy.DueLimits.ShouldAllBe(limit => limit > 0 && limit <= EventCancelledInventoryHandler.BatchSize);
        (await _store.GetSnapshotAsync(_event.Id, [], Ct)).Held.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_run_that_fails_midway_leaves_the_rooms_closed_and_a_rerun_finishes_the_job()
    {
        await HoldSeatAsync(5);
        await HoldSeatAsync(6);
        await HoldSeatAsync(7);

        var failure = await Should.ThrowAsync<AggregateException>(() => HandleAsync(new SpyStore(_store, failOnRelease: 2)));
        failure.InnerExceptions.ShouldHaveSingleItem().ShouldBeOfType<InventoryUnavailableException>();
        foreach (var phase in _event.Phases)
        {
            (await RoomStatusAsync(phase.Id)).ShouldBe("Closed");
        }

        (await _store.GetSnapshotAsync(_event.Id, [], Ct)).Held.Count.ShouldBe(2);

        await HandleAsync(_store);

        (await _store.GetSnapshotAsync(_event.Id, [], Ct)).Held.ShouldBeEmpty();
        await HandleAsync(_store);
    }

    /// <summary>Delegates to the real store and records how the handler uses it.</summary>
    private sealed class SpyStore(IInventoryStore inner, Func<Task>? onRelease = null, int failOnRelease = 0) : IInventoryStore
    {
        public List<Guid> Released { get; } = [];

        public List<int> DueLimits { get; } = [];

        public int Checks { get; private set; }

        public async Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct)
        {
            if (onRelease is not null)
            {
                await onRelease();
                Checks++;
            }

            if (Released.Count + 1 == failOnRelease)
            {
                throw new InventoryUnavailableException("simulated outage", new TimeoutException());
            }

            Released.Add(holdId);
            return await inner.ReleaseHoldAsync(eventId, holdId, eventGaTiers, ct);
        }

        public Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct)
        {
            DueLimits.Add(limit);
            return inner.DueHoldsAsync(eventId, now, limit, ct);
        }

        public Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct) => inner.CreateHoldAsync(request, ct);

        public Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct) => inner.GetUserHoldAsync(eventId, userId, ct);

        public Task<CommitOutcome> CommitAsync(Guid eventId, Guid orderId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) =>
            inner.CommitAsync(eventId, orderId, holdId, userId, seats, ga, eventGaTiers, ct);

        public Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => inner.GetSnapshotAsync(eventId, eventGaTiers, ct);

        public Task<InventoryCounts> GetCountsAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => inner.GetCountsAsync(eventId, eventGaTiers, ct);

        public Task<bool> IsWarmAsync(Guid eventId, CancellationToken ct) => inner.IsWarmAsync(eventId, ct);

        public Task<long> GetCommitFenceAsync(Guid eventId, CancellationToken ct) => inner.GetCommitFenceAsync(eventId, ct);

        public Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct) => inner.ReconcileAsync(seed, ct);
    }
}
