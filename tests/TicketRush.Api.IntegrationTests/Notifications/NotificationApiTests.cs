using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Spec 09 inbox endpoints over HTTP: the keyset list with its unread badge, and marking read one or all.</summary>
public sealed class NotificationApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly DateTimeOffset Base = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed record Account(Guid Id, string Access);

    private async Task<Account> NewAccountAsync()
    {
        var login = await _auth.SignInWithRoleAsync($"inbox-{Guid.NewGuid():N}@example.com", "Customer");
        var payload = login.AccessToken.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
        return new Account(Guid.Parse(document.RootElement.GetProperty("sub").GetString()!), login.AccessToken);
    }

    private async Task<Guid> SeedAsync(Guid userId, string key, DateTimeOffset createdAt, bool read = false)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var notice = Notification.Create(userId, NotificationType.OrderPaid, $"Title {key}", $"Body {key}", $"/account/orders/{key}", key, createdAt);
        if (read)
        {
            notice.MarkRead(createdAt);
        }

        db.Notifications.Add(notice);
        await db.SaveChangesAsync(Ct);
        return notice.Id;
    }

    private async Task<Notification> LoadAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Notifications.AsNoTracking().SingleAsync(n => n.Id == id, Ct);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? access)
    {
        var request = new HttpRequestMessage(method, path);
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

    private async Task<JsonElement> ListAsync(Account who, string query = "") =>
        await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/me/notifications{query}", who.Access));

    private static List<Guid> IdsOf(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    [Fact]
    public async Task List_is_keyset_paginated_newest_first_with_unread_count()
    {
        var me = await NewAccountAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await SeedAsync(me.Id, $"n{i}", Base.AddMinutes(i), read: i < 2));
        }

        var first = await ListAsync(me, "?limit=2");
        IdsOf(first).ShouldBe([ids[4], ids[3]]);
        first.GetProperty("unreadCount").GetInt32().ShouldBe(3);
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNullOrEmpty();

        var second = await ListAsync(me, $"?limit=2&cursor={cursor}");
        IdsOf(second).ShouldBe([ids[2], ids[1]]);
        second.GetProperty("unreadCount").GetInt32().ShouldBe(3);

        var third = await ListAsync(me, $"?limit=2&cursor={second.GetProperty("nextCursor").GetString()}");
        IdsOf(third).ShouldBe([ids[0]]);
        third.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Default_page_size_is_20()
    {
        var me = await NewAccountAsync();
        for (var i = 0; i < 22; i++)
        {
            await SeedAsync(me.Id, $"n{i}", Base.AddMinutes(i));
        }

        var page = await ListAsync(me);

        IdsOf(page).Count.ShouldBe(20);
        page.GetProperty("nextCursor").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Limit_is_capped_at_100()
    {
        var me = await NewAccountAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var i = 0; i < 105; i++)
            {
                db.Notifications.Add(Notification.Create(me.Id, NotificationType.OrderPaid, "Title", "Body", null, $"bulk-{i}", Base.AddSeconds(i)));
            }

            await db.SaveChangesAsync(Ct);
        }

        var page = await ListAsync(me, "?limit=500");

        IdsOf(page).Count.ShouldBe(100);
        page.GetProperty("nextCursor").GetString().ShouldNotBeNullOrEmpty();
        page.GetProperty("unreadCount").GetInt32().ShouldBe(105);
    }

    [Fact]
    public async Task Pages_never_repeat_or_skip_rows_that_share_a_timestamp()
    {
        var me = await NewAccountAsync();
        var seeded = new List<Guid>();
        for (var i = 0; i < 7; i++)
        {
            seeded.Add(await SeedAsync(me.Id, $"same-{i}", Base));
        }

        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await ListAsync(me, cursor is null ? "?limit=3" : $"?limit=3&cursor={cursor}");
            seen.AddRange(IdsOf(page));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);

        seen.Count.ShouldBe(7);
        seen.Distinct().Count().ShouldBe(7);

        // Postgres orders uuids bytewise, which is the order of their canonical hex text.
        seen.ShouldBe(seeded.OrderByDescending(id => id.ToString("D"), StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Items_have_exactly_the_notification_contract_shape()
    {
        var me = await NewAccountAsync();
        var id = await SeedAsync(me.Id, "shape", Base);

        var page = await ListAsync(me);

        page.EnumerateObject().Select(p => p.Name).ShouldBe(["items", "nextCursor", "unreadCount"], ignoreOrder: true);
        var item = page.GetProperty("items")[0];
        item.EnumerateObject().Select(p => p.Name).ShouldBe(["id", "type", "title", "body", "link", "read", "createdAt"], ignoreOrder: true);
        item.GetProperty("id").GetGuid().ShouldBe(id);
        item.GetProperty("type").GetString().ShouldBe("OrderPaid");
        item.GetProperty("title").GetString().ShouldBe("Title shape");
        item.GetProperty("body").GetString().ShouldBe("Body shape");
        item.GetProperty("link").GetString().ShouldBe("/account/orders/shape");
        item.GetProperty("read").GetBoolean().ShouldBeFalse();
        item.GetProperty("createdAt").GetDateTimeOffset().ShouldBe(Base);
    }

    [Fact]
    public async Task A_read_notification_without_a_link_serializes_link_as_null_and_read_as_true()
    {
        var me = await NewAccountAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notice = Notification.Create(me.Id, NotificationType.EventCancelled, "Hủy", "Sự kiện đã bị hủy", null, "no-link", Base);
            notice.MarkRead(Base);
            db.Notifications.Add(notice);
            await db.SaveChangesAsync(Ct);
        }

        var item = (await ListAsync(me)).GetProperty("items")[0];

        item.GetProperty("link").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("read").GetBoolean().ShouldBeTrue();
        item.GetProperty("type").GetString().ShouldBe("EventCancelled");
    }

    [Fact]
    public async Task List_only_returns_my_notifications()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();
        var mine = await SeedAsync(me.Id, "mine", Base);
        await SeedAsync(other.Id, "theirs", Base.AddMinutes(1));

        var page = await ListAsync(me);

        IdsOf(page).ShouldBe([mine]);
        page.GetProperty("unreadCount").GetInt32().ShouldBe(1);
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("e30")]
    public async Task A_malformed_cursor_is_a_400(string cursor)
    {
        var me = await NewAccountAsync();

        var problem = await JsonAsync(await SendAsync(HttpMethod.Get, $"/api/v1/me/notifications?cursor={cursor}", me.Access), HttpStatusCode.BadRequest);

        problem.GetProperty("errors").GetProperty("cursor").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Read_marks_only_that_notification()
    {
        var me = await NewAccountAsync();
        var target = await SeedAsync(me.Id, "a", Base);
        var untouched = await SeedAsync(me.Id, "b", Base.AddMinutes(1));

        (await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{target}/read", me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await LoadAsync(target)).ReadAt.ShouldNotBeNull();
        (await LoadAsync(untouched)).ReadAt.ShouldBeNull();
        var page = await ListAsync(me);
        page.GetProperty("unreadCount").GetInt32().ShouldBe(1);
        page.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == target).GetProperty("read").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Reading_twice_is_204_and_keeps_the_first_read_time()
    {
        var me = await NewAccountAsync();
        var id = await SeedAsync(me.Id, "a", Base);

        (await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{id}/read", me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var firstRead = (await LoadAsync(id)).ReadAt;
        (await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{id}/read", me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        firstRead.ShouldNotBeNull();
        (await LoadAsync(id)).ReadAt.ShouldBe(firstRead);
    }

    [Fact]
    public async Task Read_of_someone_elses_notification_is_404()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();
        var theirs = await SeedAsync(other.Id, "theirs", Base);

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{theirs}/read", me.Access), HttpStatusCode.NotFound);

        problem.GetProperty("code").GetString().ShouldBe("notification.not_found");
        (await LoadAsync(theirs)).ReadAt.ShouldBeNull();
    }

    [Fact]
    public async Task Read_of_an_unknown_notification_is_404_with_the_same_code()
    {
        var me = await NewAccountAsync();

        var problem = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{Guid.NewGuid()}/read", me.Access), HttpStatusCode.NotFound);

        problem.GetProperty("code").GetString().ShouldBe("notification.not_found");
    }

    [Fact]
    public async Task Read_all_clears_unread_count()
    {
        var me = await NewAccountAsync();
        for (var i = 0; i < 4; i++)
        {
            await SeedAsync(me.Id, $"n{i}", Base.AddMinutes(i), read: i == 0);
        }

        (await SendAsync(HttpMethod.Post, "/api/v1/me/notifications/read-all", me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var page = await ListAsync(me);
        page.GetProperty("unreadCount").GetInt32().ShouldBe(0);
        page.GetProperty("items").EnumerateArray().ShouldAllBe(i => i.GetProperty("read").GetBoolean());
    }

    [Fact]
    public async Task Read_all_does_not_touch_another_users_rows_or_earlier_read_times()
    {
        var me = await NewAccountAsync();
        var other = await NewAccountAsync();
        await SeedAsync(me.Id, "mine", Base);
        var theirs = await SeedAsync(other.Id, "theirs", Base);
        var alreadyRead = await SeedAsync(me.Id, "old", Base.AddMinutes(1), read: true);

        (await SendAsync(HttpMethod.Post, "/api/v1/me/notifications/read-all", me.Access)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await LoadAsync(theirs)).ReadAt.ShouldBeNull();
        (await ListAsync(other)).GetProperty("unreadCount").GetInt32().ShouldBe(1);
        (await LoadAsync(alreadyRead)).ReadAt.ShouldBe(Base.AddMinutes(1));
    }

    [Fact]
    public async Task Anonymous_is_401()
    {
        var id = Guid.NewGuid();

        (await SendAsync(HttpMethod.Get, "/api/v1/me/notifications", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, $"/api/v1/me/notifications/{id}/read", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendAsync(HttpMethod.Post, "/api/v1/me/notifications/read-all", null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
