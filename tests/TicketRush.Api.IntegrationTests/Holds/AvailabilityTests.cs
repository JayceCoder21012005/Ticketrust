using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary><c>GET /events/{id}/availability</c> reads live Redis inventory (spec 07 fills what spec 04 stubbed).</summary>
public sealed class AvailabilityTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _event;
    private Guid _gaTier;
    private Guid _vipTier;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Include(e => e.Tiers)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        _gaTier = evt.Tiers.Single(t => t.Kind == TicketKind.GeneralAdmission).Id;
        _vipTier = await scope.ServiceProvider.GetRequiredService<AppDbContext>().EventSeats
            .Where(s => s.EventId == evt.Id && s.Index == 0).Select(s => s.TierId).SingleAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private IInventoryStore Store => _factory.Services.GetRequiredService<IInventoryStore>();

    private HoldRequest Request(int[] seats, GaLine[]? ga = null) =>
        new(_event, Guid.NewGuid(), seats, ga ?? [], [_gaTier], 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));

    private async Task<JsonElement> AvailabilityAsync(HttpClient? client = null)
    {
        var response = await (client ?? _client).GetAsync($"/api/v1/events/{_event}/availability", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static int RemainingOf(JsonElement body, Guid tier) =>
        body.GetProperty("tiers").EnumerateArray().Single(t => t.GetProperty("tierId").GetGuid() == tier).GetProperty("remaining").GetInt32();

    [Fact]
    public async Task Held_and_sold_seats_are_unavailable_immediately()
    {
        await AvailabilityAsync(); // warms the event
        var before = RemainingOf(await AvailabilityAsync(), _vipTier);
        await Store.CreateHoldAsync(Request([2, 0]), Ct);
        await _factory.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase()
            .SetAddAsync(_factory.Services.GetRequiredService<RedisKeys>().InvSold(_event), 5);

        var body = await AvailabilityAsync();

        body.GetProperty("unavailable").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([0, 2, 5]);
        body.GetProperty("version").GetInt64().ShouldBeGreaterThan(0);
        RemainingOf(body, _vipTier).ShouldBe(before - 3);
    }

    [Fact]
    public async Task Ga_remaining_reflects_holds()
    {
        var capacity = RemainingOf(await AvailabilityAsync(), _gaTier);

        await Store.CreateHoldAsync(Request([], [new GaLine(_gaTier, 3)]), Ct);

        RemainingOf(await AvailabilityAsync(), _gaTier).ShouldBe(capacity - 3);
    }

    [Fact]
    public async Task A_cold_event_is_warmed_on_first_read()
    {
        (await Store.IsWarmAsync(_event, Ct)).ShouldBeFalse();

        var body = await AvailabilityAsync();

        (await Store.IsWarmAsync(_event, Ct)).ShouldBeTrue();
        RemainingOf(body, _gaTier).ShouldBe(2000);
    }

    [Fact]
    public async Task Redis_down_availability_is_503_system_busy()
    {
        await using var broken = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInventoryStore>(new ThrowingStore())));
        using var client = broken.CreateClient();

        var response = await client.GetAsync($"/api/v1/events/{_event}/availability", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("system.busy");
    }

    private sealed class ThrowingStore : IInventoryStore
    {
        private static Task<T> Down<T>() => Task.FromException<T>(new InventoryUnavailableException("down"));

        public Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct) => Down<HoldOutcome>();

        public Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<bool>();

        public Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct) => Down<ActiveHold?>();

        public Task CommitAsync(Guid eventId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<bool>();

        public Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<InventorySnapshot>();

        public Task<InventoryCounts> GetCountsAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<InventoryCounts>();

        public Task<bool> IsWarmAsync(Guid eventId, CancellationToken ct) => Down<bool>();

        public Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct) => Down<IReadOnlyList<Guid>>();

        public Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct) => Down<ReconcileReport>();
    }
}
