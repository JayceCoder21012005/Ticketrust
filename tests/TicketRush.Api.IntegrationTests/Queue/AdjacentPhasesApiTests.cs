using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

/// <summary>
/// Spec 06, adjacent phases: a presale that is open while the general sale's pre-queue window (30 min) starts. The clock is
/// fake so a buyer can join BEFORE the window and poll INSIDE it. The seeded "den-vau" event (presale with a code, then the
/// general sale) gets its phase times rewritten around the fake now.
/// </summary>
public sealed class AdjacentPhasesApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string AdjacentSlug = "den-vau-show-2026";
    private const string GeneralCode = "general-code-2026";

    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private ApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _auth = new AuthApi(_factory, AuthApi.CreateClient(_factory));
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        _host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(_time)));
        _client = _host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private sealed record Adjacent(Guid EventId, SaleWindow Presale, SaleWindow General);

    /// <summary>The presale is open now and closes when the general sale opens, <paramref name="generalOpensIn"/> from now.</summary>
    private async Task<Adjacent> ArrangeAsync(TimeSpan generalOpensIn, string? generalCode = null)
    {
        var now = _time.GetUtcNow();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.Events.Include(e => e.Phases).SingleAsync(e => e.Slug == AdjacentSlug, Ct);
        var presale = evt.Phases.Single(p => p.Name == "Presale");
        var general = evt.Phases.Single(p => p.Name != "Presale");
        var generalHash = generalCode is null ? null : scope.ServiceProvider.GetRequiredService<IAccessCodeHasher>().Hash(generalCode);

        // Order matters for the (domain-only) no-overlap rule: nothing enforces it in the database, but keep the data valid.
        await db.Set<SalePhase>().Where(p => p.Id == presale.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.OpensAt, now.AddHours(-1)).SetProperty(p => p.ClosesAt, now + generalOpensIn), Ct);
        await db.Set<SalePhase>().Where(p => p.Id == general.Id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.OpensAt, now + generalOpensIn)
                    .SetProperty(p => p.ClosesAt, now + generalOpensIn + TimeSpan.FromHours(3))
                    .SetProperty(p => p.AccessCodeHash, generalHash),
                Ct);

        return new Adjacent(
            evt.Id,
            new SaleWindow(evt.Id, presale.Id, now.AddHours(-1), now + generalOpensIn, true),
            new SaleWindow(evt.Id, general.Id, now + generalOpensIn, now + generalOpensIn + TimeSpan.FromHours(3), true));
    }

    private async Task<string> NewBuyerAsync() =>
        (await _auth.SignInWithRoleAsync($"buyer-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return _client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> JoinAsync(Guid eventId, string accessToken, string? code = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{eventId}/queue/join", accessToken, new { accessCode = code });

    private Task<HttpResponseMessage> MeAsync(Guid eventId, string accessToken) =>
        SendAsync(HttpMethod.Get, $"/api/v1/events/{eventId}/queue/me", accessToken);

    private Task<HttpResponseMessage> LeaveAsync(Guid eventId, string accessToken) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{eventId}/queue/leave", accessToken, new { });

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid UserIdOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return Guid.Parse(JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("sub").GetString()!);
    }

    private static string StatusOf(JsonElement body) => body.GetProperty("status").GetString()!;

    private static long PositionOf(JsonElement body) => body.GetProperty("position").GetInt64();

    private static DateTimeOffset OpensAtOf(JsonElement body) => body.GetProperty("opensAt").GetDateTimeOffset();

    /// <summary>Advances the clock until the general sale opens in <paramref name="remaining"/>.</summary>
    private void MoveTo(Adjacent phases, TimeSpan remaining) =>
        _time.Advance((phases.General.OpensAt - remaining) - _time.GetUtcNow());

    [Fact]
    public async Task A_new_joiner_inside_the_next_phases_window_is_queued_for_the_next_phase()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(20));
        var buyer = await NewBuyerAsync();

        // The presale has a code, the general sale none: landing in Pre without a code proves the target is the general sale.
        var joined = await BodyAsync(await JoinAsync(phases.EventId, buyer));

        StatusOf(joined).ShouldBe("Pre");
        joined.GetProperty("position").ValueKind.ShouldBe(JsonValueKind.Null);
        OpensAtOf(joined).ShouldBe(phases.General.OpensAt, TimeSpan.FromSeconds(1));
        (await BodyAsync(await MeAsync(phases.EventId, buyer))).GetProperty("status").GetString().ShouldBe("Pre");
    }

    [Fact]
    public async Task A_member_of_the_open_phase_keeps_it_when_the_next_window_starts()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(35));
        var buyer = await NewBuyerAsync();
        var joined = await BodyAsync(await JoinAsync(phases.EventId, buyer, "seed-presale"));
        StatusOf(joined).ShouldBe("Waiting");
        OpensAtOf(joined).ShouldBe(phases.Presale.OpensAt, TimeSpan.FromSeconds(1));

        MoveTo(phases, TimeSpan.FromMinutes(25)); // now inside the general sale's 30 minute window

        var me = await BodyAsync(await MeAsync(phases.EventId, buyer));
        StatusOf(me).ShouldBe("Waiting");
        PositionOf(me).ShouldBe(PositionOf(joined));
        OpensAtOf(me).ShouldBe(phases.Presale.OpensAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Leave_inside_the_window_frees_the_room_the_user_is_in()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(35));
        var buyer = await NewBuyerAsync();
        await BodyAsync(await JoinAsync(phases.EventId, buyer, "seed-presale"));
        MoveTo(phases, TimeSpan.FromMinutes(25));

        (await LeaveAsync(phases.EventId, buyer)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var room = _host.Services.GetRequiredService<IWaitingRoom>();
        var userId = UserIdOf(buyer);
        (await room.IsMemberAsync(phases.Presale, userId, Ct)).ShouldBeFalse();
        (await room.IsDoneAsync(phases.Presale, userId, Ct)).ShouldBeTrue("the presale room is the one that was left");
        (await room.IsMemberAsync(phases.General, userId, Ct)).ShouldBeFalse("nothing was ever queued for the general sale");
        (await room.IsDoneAsync(phases.General, userId, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_second_tab_rejoin_inside_the_window_returns_the_same_number()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(35));
        var early = await NewBuyerAsync();
        var first = await BodyAsync(await JoinAsync(phases.EventId, early, "seed-presale"));
        MoveTo(phases, TimeSpan.FromMinutes(25));
        var late = await NewBuyerAsync();
        var lateJoined = await BodyAsync(await JoinAsync(phases.EventId, late));
        StatusOf(lateJoined).ShouldBe("Pre");

        var earlyAgain = await BodyAsync(await JoinAsync(phases.EventId, early)); // second tab, no code, still the presale
        var lateAgain = await BodyAsync(await JoinAsync(phases.EventId, late));

        StatusOf(earlyAgain).ShouldBe("Waiting");
        PositionOf(earlyAgain).ShouldBe(PositionOf(first));
        StatusOf(lateAgain).ShouldBe("Pre");

        await _host.Services.GetRequiredService<WaitingRoomOpener>().OpenAsync(phases.General.PhaseId, Ct);
        var number = PositionOf(await BodyAsync(await MeAsync(phases.EventId, late)));
        PositionOf(await BodyAsync(await JoinAsync(phases.EventId, late))).ShouldBe(number);
    }

    [Fact]
    public async Task The_next_phases_access_code_is_the_one_checked_for_a_new_joiner()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(20), GeneralCode);
        var buyer = await NewBuyerAsync();

        await BodyAsync(await JoinAsync(phases.EventId, buyer), HttpStatusCode.Forbidden);
        await BodyAsync(await JoinAsync(phases.EventId, buyer, "seed-presale"), HttpStatusCode.Forbidden); // the open phase's code

        var joined = await BodyAsync(await JoinAsync(phases.EventId, buyer, GeneralCode));
        StatusOf(joined).ShouldBe("Pre");
        OpensAtOf(joined).ShouldBe(phases.General.OpensAt, TimeSpan.FromSeconds(1));
        StatusOf(await BodyAsync(await JoinAsync(phases.EventId, buyer))).ShouldBe("Pre"); // a member needs no code again
    }

    [Fact]
    public async Task Pre_queue_members_of_the_next_phase_get_unique_random_numbers_when_it_opens()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(20));
        var buyers = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            buyers.Add(await NewBuyerAsync());
            StatusOf(await BodyAsync(await JoinAsync(phases.EventId, buyers[i]))).ShouldBe("Pre");
        }

        await _host.Services.GetRequiredService<WaitingRoomOpener>().OpenAsync(phases.General.PhaseId, Ct);

        var positions = new List<long>();
        foreach (var buyer in buyers)
        {
            var me = await BodyAsync(await MeAsync(phases.EventId, buyer));
            StatusOf(me).ShouldBe("Waiting");
            positions.Add(PositionOf(me));
        }

        positions.Order().ShouldBe([1L, 2L, 3L, 4L, 5L, 6L]);
    }

    [Fact]
    public async Task A_user_who_left_the_open_phase_inside_the_window_sees_expired_then_the_next_phases_pre_queue_after_joining()
    {
        var phases = await ArrangeAsync(TimeSpan.FromMinutes(35));
        var buyer = await NewBuyerAsync();
        await BodyAsync(await JoinAsync(phases.EventId, buyer, "seed-presale"));
        MoveTo(phases, TimeSpan.FromMinutes(25));
        (await LeaveAsync(phases.EventId, buyer)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var afterLeave = await BodyAsync(await MeAsync(phases.EventId, buyer));
        StatusOf(afterLeave).ShouldBe("Expired");
        OpensAtOf(afterLeave).ShouldBe(phases.Presale.OpensAt, TimeSpan.FromSeconds(1));

        var rejoined = await BodyAsync(await JoinAsync(phases.EventId, buyer)); // a new joiner: the next phase, no presale code
        StatusOf(rejoined).ShouldBe("Pre");

        var afterJoin = await BodyAsync(await MeAsync(phases.EventId, buyer));
        StatusOf(afterJoin).ShouldBe("Pre");
        OpensAtOf(afterJoin).ShouldBe(phases.General.OpensAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Far_apart_phases_queue_a_new_joiner_for_the_open_phase()
    {
        var phases = await ArrangeAsync(TimeSpan.FromHours(3));
        var buyer = await NewBuyerAsync();

        var joined = await BodyAsync(await JoinAsync(phases.EventId, buyer, "seed-presale"));

        StatusOf(joined).ShouldBe("Waiting");
        OpensAtOf(joined).ShouldBe(phases.Presale.OpensAt, TimeSpan.FromSeconds(1));
        StatusOf(await BodyAsync(await MeAsync(phases.EventId, buyer))).ShouldBe("Waiting");
    }

    [Fact]
    public async Task Single_phase_events_behave_as_before()
    {
        Guid eventId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            eventId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct)).Id;
        }

        var buyer = await NewBuyerAsync();
        (await MeAsync(eventId, buyer)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var joined = await BodyAsync(await JoinAsync(eventId, buyer));
        StatusOf(joined).ShouldBe("Waiting");
        PositionOf(await BodyAsync(await JoinAsync(eventId, buyer))).ShouldBeLessThanOrEqualTo(PositionOf(joined));
        StatusOf(await BodyAsync(await MeAsync(eventId, buyer))).ShouldBe("Waiting");

        (await LeaveAsync(eventId, buyer)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        StatusOf(await BodyAsync(await MeAsync(eventId, buyer))).ShouldBe("Expired");
    }

    [Fact]
    public async Task The_seeded_opening_soon_event_is_still_a_single_phase_pre_queue()
    {
        Guid eventId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            eventId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.SingleAsync(e => e.Slug == SeedEventCatalog.OpeningSoonSlug, Ct)).Id;
        }

        var buyer = await NewBuyerAsync();

        StatusOf(await BodyAsync(await JoinAsync(eventId, buyer))).ShouldBe("Pre");
        StatusOf(await BodyAsync(await MeAsync(eventId, buyer))).ShouldBe("Pre");
    }
}
