using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Spec 09 follow endpoints over HTTP against the dev seed (published, draft, ended and cancelled events).</summary>
public sealed class FollowApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;
    private Guid _published;
    private Guid _draft;
    private Guid _ended;
    private Guid _cancelled;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _published = await IdOfAsync(db, "son-tung-mtp-live");
        _draft = await IdOfAsync(db, "rock-storm-2026");
        _ended = await IdOfAsync(db, "jazz-night-saigon");
        _cancelled = await IdOfAsync(db, "monsoon-festival-2026");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static Task<Guid> IdOfAsync(AppDbContext db, string slug) => db.Events.AsNoTracking().Where(e => e.Slug == slug).Select(e => e.Id).SingleAsync(Ct);

    private sealed record Account(Guid Id, string Access);

    private async Task<Account> NewAccountAsync()
    {
        var login = await _auth.SignInWithRoleAsync($"follow-{Guid.NewGuid():N}@example.com", "Customer");
        var payload = login.AccessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return new Account(Guid.Parse(document.RootElement.GetProperty("sub").GetString()!), login.AccessToken);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, Guid eventId, string? access)
    {
        var request = new HttpRequestMessage(method, $"/api/v1/events/{eventId}/follow");
        if (access is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        }

        return _client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<int> RowsAsync(Guid userId, Guid eventId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().EventFollows.CountAsync(f => f.UserId == userId && f.EventId == eventId, Ct);
    }

    private async Task<bool> IsFollowingAsync(Account who, Guid eventId) =>
        (await JsonAsync(await SendAsync(HttpMethod.Get, eventId, who.Access))).GetProperty("following").GetBoolean();

    [Fact]
    public async Task My_follows_lists_upcoming_followed_events_only_in_start_order()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var later = await IdOfAsync(db, SeedEventCatalog.OpeningSoonSlug);
            var ending = await IdOfAsync(db, "v-league-ha-noi-vs-cong-an");
            foreach (var id in new[] { later, _published, ending })
            {
                (await SendAsync(HttpMethod.Post, id, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            }

            (await SendAsync(HttpMethod.Post, ending, other.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
            await db.Database.ExecuteSqlAsync($"UPDATE events SET starts_at = now() - interval '3 hours', ends_at = now() - interval '1 hour' WHERE id = {ending}", Ct);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/me/follows");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", me.Access);
        var follows = await JsonAsync(await _client.SendAsync(request, Ct));

        var slugs = follows.EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToList();
        slugs.ShouldBe(["son-tung-mtp-live", SeedEventCatalog.OpeningSoonSlug]);
        (await _client.GetAsync("/api/v1/me/follows", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Follow_twice_is_204_and_one_row()
    {
        var me = await NewAccountAsync();

        (await SendAsync(HttpMethod.Post, _published, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Post, _published, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RowsAsync(me.Id, _published)).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_follows_all_answer_204_and_leave_one_row()
    {
        var me = await NewAccountAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => SendAsync(HttpMethod.Post, _published, me.Access)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.NoContent);
        (await RowsAsync(me.Id, _published)).ShouldBe(1);
    }

    [Fact]
    public async Task Unfollow_removes_the_row()
    {
        var me = await NewAccountAsync();
        await SendAsync(HttpMethod.Post, _published, me.Access);

        (await SendAsync(HttpMethod.Delete, _published, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RowsAsync(me.Id, _published)).ShouldBe(0);
    }

    [Fact]
    public async Task Unfollow_when_not_following_is_204()
    {
        var me = await NewAccountAsync();

        (await SendAsync(HttpMethod.Delete, _published, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SendAsync(HttpMethod.Delete, _published, me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Unfollow_leaves_other_users_follows_alone()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();
        await SendAsync(HttpMethod.Post, _published, me.Access);
        await SendAsync(HttpMethod.Post, _published, other.Access);

        await SendAsync(HttpMethod.Delete, _published, me.Access);

        (await RowsAsync(other.Id, _published)).ShouldBe(1);
        (await IsFollowingAsync(other, _published)).ShouldBeTrue();
    }

    [Fact]
    public async Task Follow_a_draft_event_is_409()
    {
        var me = await NewAccountAsync();

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, _draft, me.Access), HttpStatusCode.Conflict);

        problem.GetProperty("code").GetString().ShouldBe("event.not_followable");
        (await RowsAsync(me.Id, _draft)).ShouldBe(0);
    }

    [Theory]
    [InlineData("ended")]
    [InlineData("cancelled")]
    public async Task Follow_an_ended_or_cancelled_event_is_409(string which)
    {
        var me = await NewAccountAsync();

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, which == "ended" ? _ended : _cancelled, me.Access), HttpStatusCode.Conflict);

        problem.GetProperty("code").GetString().ShouldBe("event.not_followable");
    }

    [Fact]
    public async Task Follow_an_unknown_event_is_404()
    {
        var me = await NewAccountAsync();

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, Guid.NewGuid(), me.Access), HttpStatusCode.NotFound);

        problem.GetProperty("code").GetString().ShouldBe("event.not_found");
    }

    [Fact]
    public async Task Follow_state_reflects_follow_and_unfollow()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();

        (await IsFollowingAsync(me, _published)).ShouldBeFalse();
        await SendAsync(HttpMethod.Post, _published, me.Access);
        (await IsFollowingAsync(me, _published)).ShouldBeTrue();
        (await IsFollowingAsync(other, _published)).ShouldBeFalse();
        await SendAsync(HttpMethod.Delete, _published, me.Access);
        (await IsFollowingAsync(me, _published)).ShouldBeFalse();
    }

    [Fact]
    public async Task Follow_state_body_is_exactly_following()
    {
        var me = await NewAccountAsync();

        var body = await JsonAsync(await SendAsync(HttpMethod.Get, _published, me.Access));

        body.EnumerateObject().Select(p => p.Name).ShouldBe(["following"]);
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        (await SendAsync(HttpMethod.Get, _published, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, _published, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Delete, _published, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
