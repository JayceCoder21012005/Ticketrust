using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Waitlist;

/// <summary>Spec 13 waitlist endpoints over HTTP against the dev seed; "sold out" is made by marking every seat and GA ticket sold in Redis.</summary>
public sealed class WaitlistApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _event;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        _event = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking()
            .Where(e => e.Slug == "son-tung-mtp-live").Select(e => e.Id).SingleAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_event, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task SellOutAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seats = await db.EventSeats.AsNoTracking().Where(s => s.EventId == _event).Select(s => s.Index).ToListAsync(Ct);
        var gaTiers = await db.Events.AsNoTracking().Where(e => e.Id == _event)
            .SelectMany(e => e.Tiers).Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id).ToListAsync(Ct);
        var keys = scope.ServiceProvider.GetRequiredService<RedisKeys>();
        var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.SetAddAsync(keys.InvSold(_event), [.. seats.Select(s => (RedisValue)s)]);
        foreach (var tier in gaTiers)
        {
            await redis.StringSetAsync(keys.InvGa(_event, tier), 0);
        }
    }

    private async Task<string> NewAccessAsync() => (await _auth.SignInWithRoleAsync($"wait-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string access, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return _client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> JoinAsync(string access, int quantity = 1, Guid? tierId = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/events/{_event}/waitlist", access, new { tierId, quantity });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static async Task ShouldBeErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code) =>
        (await JsonAsync(response, status)).GetProperty("code").GetString().ShouldBe(code);

    [Fact]
    public async Task Joining_while_tickets_remain_is_not_available()
    {
        await ShouldBeErrorAsync(await JoinAsync(await NewAccessAsync()), HttpStatusCode.Conflict, "waitlist.not_available");
    }

    [Fact]
    public async Task Joining_a_sold_out_event_puts_users_in_line_in_order()
    {
        await SellOutAsync();
        var first = await NewAccessAsync();
        var second = await NewAccessAsync();

        var joined = await JsonAsync(await JoinAsync(first, quantity: 2));
        joined.GetProperty("status").GetString().ShouldBe("Waiting");
        joined.GetProperty("position").GetInt32().ShouldBe(1);
        joined.GetProperty("quantity").GetInt32().ShouldBe(2);
        (await JsonAsync(await JoinAsync(second))).GetProperty("position").GetInt32().ShouldBe(2);

        var mine = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_event}/waitlist/me", second));
        mine.GetProperty("position").GetInt32().ShouldBe(2);
        var list = await JsonAsync(await SendAsync(HttpMethod.Get, "/api/v1/me/waitlist", second));
        list.GetArrayLength().ShouldBe(1);
        list[0].GetProperty("eventSlug").GetString().ShouldBe("son-tung-mtp-live");
    }

    [Fact]
    public async Task Joining_twice_is_already_joined_and_leaving_lets_the_next_move_up()
    {
        await SellOutAsync();
        var first = await NewAccessAsync();
        var second = await NewAccessAsync();
        (await JoinAsync(first)).EnsureSuccessStatusCode();
        (await JoinAsync(second)).EnsureSuccessStatusCode();

        await ShouldBeErrorAsync(await JoinAsync(first), HttpStatusCode.Conflict, "waitlist.already_joined");
        (await SendAsync(HttpMethod.Delete, $"/api/v1/events/{_event}/waitlist/me", first)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await ShouldBeErrorAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_event}/waitlist/me", first), HttpStatusCode.NotFound, "waitlist.not_joined");
        (await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/events/{_event}/waitlist/me", second))).GetProperty("position").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public async Task Quantity_outside_one_to_four_is_a_validation_error(int quantity)
    {
        await SellOutAsync();
        await ShouldBeErrorAsync(await JoinAsync(await NewAccessAsync(), quantity), HttpStatusCode.BadRequest, "waitlist.invalid_quantity");
    }

    [Fact]
    public async Task Someone_elses_or_an_unknown_offer_is_not_found()
    {
        await ShouldBeErrorAsync(
            await SendAsync(HttpMethod.Get, $"/api/v1/waitlist/offers/{Guid.NewGuid()}", await NewAccessAsync()), HttpStatusCode.NotFound, "waitlist.offer_not_found");
    }

    [Fact]
    public async Task Accepting_without_a_live_offer_is_offer_expired()
    {
        await SellOutAsync();
        var access = await NewAccessAsync();
        var entry = (await JsonAsync(await JoinAsync(access))).GetProperty("entryId").GetGuid();

        await ShouldBeErrorAsync(
            await SendAsync(HttpMethod.Post, $"/api/v1/waitlist/offers/{entry}/accept", access), HttpStatusCode.Conflict, "waitlist.offer_expired");
    }

    [Fact]
    public async Task The_waitlist_needs_a_signed_in_user()
    {
        using var response = await _client.PostAsJsonAsync($"/api/v1/events/{_event}/waitlist", new { quantity = 1 }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
