using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Spec 07 hold endpoints over HTTP against the dev seed (son-tung is on sale with a queued phase).</summary>
public sealed class HoldApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _event;
    private Guid _phase;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.Include(e => e.Phases)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        (_event, _phase) = (evt.Id, evt.Phases.OrderBy(p => p.OpensAt).First().Id);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed record Buyer(string AccessToken, string Admission);

    private async Task<Buyer> AdmittedBuyerAsync(HttpClient? client = null)
    {
        var access = (await _auth.SignInWithRoleAsync($"buyer-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{_event}/queue/join", access, new { captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }, client: client)).EnsureSuccessStatusCode();
        var admitter = _factory.Services.GetRequiredService<WaitingRoomAdmitter>();
        for (var i = 0; i < 200; i++)
        {
            await admitter.TickAsync(_phase, Ct);
            var me = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_event}/queue/me", access, client: client));
            if (me.GetProperty("status").GetString() == "Admitted")
            {
                return new Buyer(access, me.GetProperty("admissionToken").GetString()!);
            }
        }

        throw new InvalidOperationException("The buyer was never admitted.");
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string access, object? body = null, string? admission = null, string? key = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (admission is not null)
        {
            request.Headers.Add("X-Admission-Token", admission);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return (client ?? _client).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> HoldAsync(Buyer buyer, object body, string? key = null, string? admission = null, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{_event}/holds", buyer.AccessToken, body, admission ?? buyer.Admission, key ?? Guid.NewGuid().ToString(), client);

    private Task<HttpResponseMessage> MineAsync(Buyer buyer, HttpMethod? method = null) =>
        SendAsync(method ?? HttpMethod.Get, $"/api/v1/events/{_event}/holds/me", buyer.AccessToken);

    /// <summary>Held or sold seats straight from Redis (the public availability endpoint is covered by AvailabilityTests).</summary>
    private async Task<int[]> UnavailableAsync()
    {
        var snapshot = await _factory.Services.GetRequiredService<IInventoryStore>().GetSnapshotAsync(_event, [], Ct);
        return [.. snapshot.Held.Concat(snapshot.Sold)];
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task ShouldFailWithAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("code").GetString().ShouldBe(code);

    [Fact]
    public async Task Holding_seats_returns_the_hold_with_prices_and_server_time()
    {
        var buyer = await AdmittedBuyerAsync();

        var hold = await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 3, 1 }, replaceExisting = false }));

        hold.GetProperty("seats").EnumerateArray().Select(s => s.GetProperty("index").GetInt32()).ShouldBe([1, 3]);
        hold.GetProperty("seats")[0].GetProperty("price").GetInt64().ShouldBeGreaterThan(0);
        hold.GetProperty("total").GetInt64().ShouldBe(hold.GetProperty("seats").EnumerateArray().Sum(s => s.GetProperty("price").GetInt64()));
        hold.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(hold.GetProperty("serverNow").GetDateTimeOffset().AddMinutes(7));
    }

    [Fact]
    public async Task Without_an_admission_token_is_403_admission_required()
    {
        var buyer = await AdmittedBuyerAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{_event}/holds") { Content = JsonContent.Create(new { seatIndexes = new[] { 1 } }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer.AccessToken);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        await ShouldFailWithAsync(await _client.SendAsync(request, Ct), HttpStatusCode.Forbidden, "admission.required");
    }

    [Fact]
    public async Task Another_buyers_token_is_403_admission_invalid()
    {
        var owner = await AdmittedBuyerAsync();
        var thief = await AdmittedBuyerAsync();

        await ShouldFailWithAsync(await HoldAsync(thief, new { seatIndexes = new[] { 1 } }, admission: owner.Admission), HttpStatusCode.Forbidden, "admission.invalid");
    }

    [Fact]
    public async Task Missing_idempotency_key_is_400()
    {
        var buyer = await AdmittedBuyerAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{_event}/holds") { Content = JsonContent.Create(new { seatIndexes = new[] { 1 } }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyer.AccessToken);
        request.Headers.Add("X-Admission-Token", buyer.Admission);

        await ShouldFailWithAsync(await _client.SendAsync(request, Ct), HttpStatusCode.BadRequest, "idempotency.key_required");
    }

    [Fact]
    public async Task A_retry_with_the_same_key_returns_the_same_hold()
    {
        var buyer = await AdmittedBuyerAsync();
        var key = Guid.NewGuid().ToString();

        var first = await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 20 } }, key));
        var retry = await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 20 } }, key));

        retry.GetProperty("holdId").GetGuid().ShouldBe(first.GetProperty("holdId").GetGuid());
    }

    [Fact]
    public async Task Taken_seats_are_409_with_the_unavailable_indexes()
    {
        var first = await AdmittedBuyerAsync();
        var second = await AdmittedBuyerAsync();
        await JsonAsync(await HoldAsync(first, new { seatIndexes = new[] { 30, 31 } }));

        var problem = await JsonAsync(await HoldAsync(second, new { seatIndexes = new[] { 31, 32 } }), HttpStatusCode.Conflict);

        problem.GetProperty("code").GetString().ShouldBe("hold.seats_unavailable");
        problem.GetProperty("unavailable").EnumerateArray().Select(e => e.GetInt32()).ShouldBe([31]);
    }

    [Fact]
    public async Task A_second_hold_needs_replace_and_replace_swaps_it()
    {
        var buyer = await AdmittedBuyerAsync();
        await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 40 } }));

        await ShouldFailWithAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 41 } }), HttpStatusCode.Conflict, "hold.already_exists");
        var swapped = await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 41 }, replaceExisting = true }));

        swapped.GetProperty("seats")[0].GetProperty("index").GetInt32().ShouldBe(41);
        var unavailable = await UnavailableAsync();
        unavailable.ShouldContain(41);
        unavailable.ShouldNotContain(40);
    }

    [Fact]
    public async Task Get_me_returns_the_hold_or_404()
    {
        var buyer = await AdmittedBuyerAsync();
        await ShouldFailWithAsync(await MineAsync(buyer), HttpStatusCode.NotFound, "hold.not_found");

        var hold = await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 50 } }));

        (await JsonAsync(await MineAsync(buyer))).GetProperty("holdId").GetGuid().ShouldBe(hold.GetProperty("holdId").GetGuid());
    }

    [Fact]
    public async Task Delete_me_is_204_twice_and_frees_the_seats()
    {
        var buyer = await AdmittedBuyerAsync();
        await JsonAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 60 } }));

        (await MineAsync(buyer, HttpMethod.Delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MineAsync(buyer, HttpMethod.Delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await UnavailableAsync()).ShouldNotContain(60);
    }

    [Fact]
    public async Task Releasing_an_expired_hold_is_204_and_keeps_the_new_owners_seat()
    {
        var first = await AdmittedBuyerAsync();
        var second = await AdmittedBuyerAsync();
        var hold = await JsonAsync(await HoldAsync(first, new { seatIndexes = new[] { 70 } }));
        // The sweeper released the expired hold and someone else took the seat.
        await _factory.Services.GetRequiredService<IInventoryStore>().ReleaseHoldAsync(_event, hold.GetProperty("holdId").GetGuid(), [], Ct);
        await JsonAsync(await HoldAsync(second, new { seatIndexes = new[] { 70 } }));

        (await MineAsync(first, HttpMethod.Delete)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await UnavailableAsync()).ShouldContain(70);
        (await JsonAsync(await MineAsync(second))).GetProperty("seats")[0].GetProperty("index").GetInt32().ShouldBe(70);
    }

    [Fact]
    public async Task Redis_down_is_503_system_busy_and_nothing_reaches_the_database()
    {
        var buyer = await AdmittedBuyerAsync();
        await using var broken = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInventoryStore, UnavailableInventory>()));
        using var client = broken.CreateClient();
        var before = await CountRowsAsync();

        await ShouldFailWithAsync(await HoldAsync(buyer, new { seatIndexes = new[] { 80 } }, client: client), HttpStatusCode.ServiceUnavailable, "system.busy");

        (await CountRowsAsync()).ShouldBe(before);
    }

    private async Task<int> CountRowsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM outbox_messages").SingleAsync(Ct);
    }

    private sealed class UnavailableInventory : IInventoryStore
    {
        private static Task<T> Down<T>() => Task.FromException<T>(new InventoryUnavailableException("down"));

        public Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct) => Down<HoldOutcome>();

        public Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<bool>();

        public Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct) => Down<ActiveHold?>();

        public Task<bool> HoldRecordedAsync(Guid eventId, Guid holdId, Guid userId, CancellationToken ct) => Down<bool>();

        public Task<CommitOutcome> CommitAsync(Guid eventId, Guid orderId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<CommitOutcome>();

        public Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<InventorySnapshot>();

        public Task<InventoryCounts> GetCountsAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) => Down<InventoryCounts>();

        public Task<bool> IsWarmAsync(Guid eventId, CancellationToken ct) => Down<bool>();

        public Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct) => Down<IReadOnlyList<Guid>>();

        public Task<long> GetCommitFenceAsync(Guid eventId, CancellationToken ct) => Down<long>();

        public Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct) => Down<ReconcileReport>();
    }
}
