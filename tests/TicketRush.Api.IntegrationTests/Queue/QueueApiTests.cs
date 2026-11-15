using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

/// <summary>Spec 06 over HTTP against the dev seed: son-tung is on sale (queued), the "opening soon" event is in its pre-queue.</summary>
public sealed class QueueApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _onSaleEvent;
    private Guid _onSalePhase;
    private Guid _preQueueEvent;
    private Guid _preQueuePhase;
    private Guid _futureEvent;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (_onSaleEvent, _onSalePhase) = await PhaseOfAsync(db, "son-tung-mtp-live");
        (_preQueueEvent, _preQueuePhase) = await PhaseOfAsync(db, SeedEventCatalog.OpeningSoonSlug);
        (_futureEvent, _) = await PhaseOfAsync(db, "den-vau-show-2026");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static async Task<(Guid EventId, Guid PhaseId)> PhaseOfAsync(AppDbContext db, string slug)
    {
        var evt = await db.Events.Include(e => e.Phases).SingleAsync(e => e.Slug == slug, Ct);
        return (evt.Id, evt.Phases.OrderBy(p => p.OpensAt).First().Id);
    }

    private async Task<string> NewBuyerAsync() =>
        (await _auth.SignInWithRoleAsync($"buyer-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? accessToken, object? body = null, string? admissionToken = null, HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        if (admissionToken is not null)
        {
            request.Headers.Add("X-Admission-Token", admissionToken);
        }

        return (client ?? _client).SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> JoinAsync(Guid eventId, string accessToken, object? body = null, HttpClient? client = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{eventId}/queue/join", accessToken, body ?? new { }, client: client);

    private Task<HttpResponseMessage> MeAsync(Guid eventId, string accessToken) =>
        SendAsync(HttpMethod.Get, $"/api/v1/events/{eventId}/queue/me", accessToken);

    private Task<HttpResponseMessage> LeaveAsync(Guid eventId, string accessToken) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{eventId}/queue/leave", accessToken, new { });

    private Task<HttpResponseMessage> GuardedAsync(Guid eventId, string accessToken, string? admissionToken) =>
        SendAsync(HttpMethod.Get, $"/_test/auth/admission/{eventId}", accessToken, admissionToken: admissionToken);

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task ShouldFailWithAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var body = await BodyAsync(response, status);
        body.GetProperty("code").GetString().ShouldBe(code);
    }

    /// <summary>Lets the admitter run until the user's number is reached, then reads their status.</summary>
    private async Task<JsonElement> AdmitAsync(Guid eventId, Guid phaseId, string accessToken)
    {
        var admitter = _factory.Services.GetRequiredService<WaitingRoomAdmitter>();
        for (var i = 0; i < 200; i++)
        {
            await admitter.TickAsync(phaseId, Ct);
            var me = await BodyAsync(await MeAsync(eventId, accessToken));
            if (me.GetProperty("status").GetString() == "Admitted")
            {
                return me;
            }
        }

        throw new InvalidOperationException("The user was never admitted.");
    }

    [Fact]
    public async Task Join_twice_from_two_tabs_returns_same_number_and_token()
    {
        var buyer = await NewBuyerAsync();

        var first = await BodyAsync(await JoinAsync(_onSaleEvent, buyer));
        var second = await BodyAsync(await JoinAsync(_onSaleEvent, buyer));
        first.GetProperty("status").GetString().ShouldBe("Waiting");
        second.GetProperty("position").GetInt64().ShouldBeLessThanOrEqualTo(first.GetProperty("position").GetInt64());

        await AdmitAsync(_onSaleEvent, _onSalePhase, buyer);
        var tabs = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => MeAsync(_onSaleEvent, buyer)));
        var bodies = await Task.WhenAll(tabs.Select(t => BodyAsync(t)));

        bodies.ShouldAllBe(b => b.GetProperty("status").GetString() == "Admitted");
        bodies.Select(b => b.GetProperty("admissionToken").GetString()).Distinct().Count().ShouldBe(1);
        bodies[0].GetProperty("tokenExpiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(9));
    }

    [Fact]
    public async Task Pre_queue_then_opening_late_joiner_gets_number_above_every_pre_user()
    {
        var early = new[] { await NewBuyerAsync(), await NewBuyerAsync(), await NewBuyerAsync() };
        foreach (var buyer in early)
        {
            var joined = await BodyAsync(await JoinAsync(_preQueueEvent, buyer));
            joined.GetProperty("status").GetString().ShouldBe("Pre");
            joined.GetProperty("position").ValueKind.ShouldBe(JsonValueKind.Null);
            joined.GetProperty("pollAfterMs").GetInt32().ShouldBeInRange(1_000, 120_000);
        }

        await _factory.Services.GetRequiredService<WaitingRoomOpener>().OpenAsync(_preQueuePhase, Ct);
        var late = await NewBuyerAsync();
        var lateBody = await BodyAsync(await JoinAsync(_preQueueEvent, late));

        lateBody.GetProperty("status").GetString().ShouldBe("Waiting");
        lateBody.GetProperty("position").GetInt64().ShouldBe(4);
        var positions = new List<long>();
        foreach (var buyer in early)
        {
            positions.Add((await BodyAsync(await MeAsync(_preQueueEvent, buyer))).GetProperty("position").GetInt64());
        }

        positions.Order().ShouldBe([1L, 2L, 3L]);
    }

    [Fact]
    public async Task Token_used_by_another_account_is_403_admission_invalid()
    {
        var owner = await NewBuyerAsync();
        var thief = await NewBuyerAsync();
        await JoinAsync(_onSaleEvent, owner);
        var token = (await AdmitAsync(_onSaleEvent, _onSalePhase, owner)).GetProperty("admissionToken").GetString()!;

        (await GuardedAsync(_onSaleEvent, owner, token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldFailWithAsync(await GuardedAsync(_onSaleEvent, thief, token), HttpStatusCode.Forbidden, "admission.invalid");
    }

    [Fact]
    public async Task Missing_garbage_and_other_event_tokens_are_rejected()
    {
        var buyer = await NewBuyerAsync();
        await JoinAsync(_onSaleEvent, buyer);
        var token = (await AdmitAsync(_onSaleEvent, _onSalePhase, buyer)).GetProperty("admissionToken").GetString()!;

        await ShouldFailWithAsync(await GuardedAsync(_onSaleEvent, buyer, null), HttpStatusCode.Forbidden, "admission.required");
        await ShouldFailWithAsync(await GuardedAsync(_onSaleEvent, buyer, "garbage"), HttpStatusCode.Forbidden, "admission.invalid");
        await ShouldFailWithAsync(await GuardedAsync(_preQueueEvent, buyer, token), HttpStatusCode.Forbidden, "admission.invalid");
    }

    [Fact]
    public async Task Admission_is_revoked_by_leave_and_an_old_token_stays_dead_after_rejoining()
    {
        var buyer = await NewBuyerAsync();
        await JoinAsync(_onSaleEvent, buyer);
        var oldToken = (await AdmitAsync(_onSaleEvent, _onSalePhase, buyer)).GetProperty("admissionToken").GetString()!;

        (await LeaveAsync(_onSaleEvent, buyer)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldFailWithAsync(await GuardedAsync(_onSaleEvent, buyer, oldToken), HttpStatusCode.Forbidden, "admission.invalid");
        (await BodyAsync(await MeAsync(_onSaleEvent, buyer))).GetProperty("status").GetString().ShouldBe("Expired");

        await JoinAsync(_onSaleEvent, buyer);
        var newToken = (await AdmitAsync(_onSaleEvent, _onSalePhase, buyer)).GetProperty("admissionToken").GetString()!;

        newToken.ShouldNotBe(oldToken);
        await ShouldFailWithAsync(await GuardedAsync(_onSaleEvent, buyer, oldToken), HttpStatusCode.Forbidden, "admission.invalid");
        (await GuardedAsync(_onSaleEvent, buyer, newToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Me_never_queries_postgres_after_the_first_call()
    {
        var buyer = await NewBuyerAsync();
        await JoinAsync(_onSaleEvent, buyer);
        _factory.Queries.Reset();

        for (var i = 0; i < 5; i++)
        {
            (await MeAsync(_onSaleEvent, buyer)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        _factory.Queries.Matching(_ => true).ShouldBeEmpty();
    }

    [Fact]
    public async Task Redis_down_returns_system_busy()
    {
        var buyer = await NewBuyerAsync();
        await using var broken = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IWaitingRoom, UnavailableWaitingRoom>()));
        using var client = broken.CreateClient();

        await ShouldFailWithAsync(await JoinAsync(_onSaleEvent, buyer, client: client), HttpStatusCode.ServiceUnavailable, "system.busy");
        await ShouldFailWithAsync(
            await SendAsync(HttpMethod.Get, $"/api/v1/events/{_onSaleEvent}/queue/me", buyer, client: client),
            HttpStatusCode.ServiceUnavailable,
            "system.busy");
    }

    [Fact]
    public async Task Unauthenticated_unconfirmed_unknown_and_not_yet_open_are_rejected()
    {
        (await SendAsync(HttpMethod.Post, $"/api/v1/events/{_onSaleEvent}/queue/join", null, new { })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var email = $"unconfirmed-{Guid.NewGuid():N}@example.com";
        (await _auth.RegisterAsync(email)).EnsureSuccessStatusCode();
        var unconfirmed = (await LoginResult.ReadAsync(await _auth.LoginAsync(email))).AccessToken;
        await ShouldFailWithAsync(await JoinAsync(_onSaleEvent, unconfirmed), HttpStatusCode.Forbidden, "queue.email_not_confirmed");

        var buyer = await NewBuyerAsync();
        await ShouldFailWithAsync(await JoinAsync(Guid.NewGuid(), buyer), HttpStatusCode.NotFound, "event.not_found");
        await ShouldFailWithAsync(await JoinAsync(_futureEvent, buyer), HttpStatusCode.Conflict, "queue.not_open");
        await ShouldFailWithAsync(await MeAsync(_onSaleEvent, buyer), HttpStatusCode.NotFound, "queue.not_joined");
    }

    private sealed class UnavailableWaitingRoom : IWaitingRoom
    {
        public Task<JoinResult> JoinAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<ClaimResult> StatusAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task LeaveAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<bool> IsMemberAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<bool> IsDoneAsync(SaleWindow sale, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<bool> IsActiveAsync(Guid phaseId, Guid userId, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<bool> IsCurrentTokenAsync(Guid phaseId, Guid userId, string token, CancellationToken ct) => throw new QueueUnavailableException("down");

        public Task<double> GetRateEmaAsync(Guid phaseId, CancellationToken ct) => throw new QueueUnavailableException("down");
    }
}
