using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Organizer;

/// <summary>Spec 05: venues, drafts, details and cover upload.</summary>
public sealed class OrganizerEventsTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private OrganizerApi _api = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(TestContext.Current.CancellationToken);
        }

        _api = await OrganizerApi.SignInAsync(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static Guid Id(JsonElement body) => body.GetProperty("id").GetGuid();

    [Theory]
    [InlineData("GET", "/events")]
    [InlineData("GET", "/venues")]
    [InlineData("POST", "/events")]
    public async Task Customer_gets_403_on_organizer_routes(string method, string path)
    {
        var customer = await OrganizerApi.SignInAsync(_factory, _client, "Customer");

        var response = await customer.SendAsync(new HttpMethod(method), path, method == "GET" ? null : new { });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Anonymous_gets_401()
    {
        (await _client.GetAsync("/api/v1/organizer/events", TestContext.Current.CancellationToken)).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Other_organizers_event_returns_404()
    {
        var draft = await _api.CreateDraftAsync();
        var other = await OrganizerApi.SignInAsync(_factory, _client);

        var get = await other.GetAsync($"/events/{Id(draft)}");
        var put = await other.PutAsync($"/events/{Id(draft)}/info", new { version = draft.GetProperty("version").GetString(), title = "Chiếm", description = "", lineup = Array.Empty<string>() });

        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OrganizerApi.CodeAsync(get)).ShouldBe("event.not_found");
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OrganizerApi.BodyAsync(await other.GetAsync("/events"))).EnumerateArray().ShouldNotContain(e => Id(e) == Id(draft));
    }

    [Fact]
    public async Task Create_draft_returns_slug_and_draft_status()
    {
        var draft = await _api.CreateDraftAsync("Đêm nhạc Sơn Tùng");

        draft.GetProperty("slug").GetString().ShouldBe("dem-nhac-son-tung");
        draft.GetProperty("status").GetString().ShouldBe("Draft");
        draft.GetProperty("version").GetString().ShouldNotBeNullOrEmpty();
        draft.GetProperty("tiers").GetArrayLength().ShouldBe(0);
        draft.GetProperty("layout").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Duplicate_title_gets_suffixed_slug()
    {
        var first = await _api.CreateDraftAsync("Hoà nhạc mùa đông");
        var second = await _api.CreateDraftAsync("Hoà nhạc mùa đông");
        var third = await _api.CreateDraftAsync("Hoà nhạc mùa đông");

        first.GetProperty("slug").GetString().ShouldBe("hoa-nhac-mua-dong");
        second.GetProperty("slug").GetString().ShouldBe("hoa-nhac-mua-dong-2");
        third.GetProperty("slug").GetString().ShouldBe("hoa-nhac-mua-dong-3");
    }

    [Fact]
    public async Task Create_draft_validates_its_fields()
    {
        var venue = Id(await _api.VenueAsync());

        var response = await _api.PostAsync("/events", new { title = "ab", category = "karaoke", venueId = venue, startsAt = OrganizerApi.Start, endsAt = OrganizerApi.Start.AddHours(-1) });

        var body = await OrganizerApi.BodyAsync(response, 400);
        var errors = body.GetProperty("errors");
        errors.TryGetProperty("title", out _).ShouldBeTrue();
        errors.TryGetProperty("category", out _).ShouldBeTrue();
        errors.TryGetProperty("endsAt", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Venue_of_another_organizer_returns_venue_not_found()
    {
        var other = await OrganizerApi.SignInAsync(_factory, _client);
        var theirs = Id(await other.VenueAsync("Sân khấu riêng"));

        var response = await _api.PostAsync("/events", new { title = "Mượn sân", category = "concert", venueId = theirs, startsAt = OrganizerApi.Start, endsAt = OrganizerApi.Start.AddHours(2) });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("venue.not_found");
    }

    [Fact]
    public async Task Shared_venues_are_listed_for_every_organizer()
    {
        var mine = Id(await _api.VenueAsync("Nhà văn hoá phường"));
        var other = await OrganizerApi.SignInAsync(_factory, _client);

        var list = (await OrganizerApi.BodyAsync(await _api.GetAsync("/venues"))).EnumerateArray().ToList();
        var otherList = (await OrganizerApi.BodyAsync(await other.GetAsync("/venues"))).EnumerateArray().ToList();

        list.Count(v => v.GetProperty("shared").GetBoolean()).ShouldBe(3);
        list.ShouldContain(v => Id(v) == mine && !v.GetProperty("shared").GetBoolean());
        otherList.ShouldNotContain(v => Id(v) == mine);
        otherList.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Venue_map_url_must_be_http()
    {
        var response = await _api.PostAsync("/venues", new { name = "X", address = "Y", city = "Huế", mapUrl = "javascript:alert(1)", capacity = 10 });

        (await OrganizerApi.BodyAsync(response, 400)).GetProperty("errors").TryGetProperty("mapUrl", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Update_info_on_draft_saves_text_and_returns_a_new_version()
    {
        var draft = await _api.CreateDraftAsync();

        var response = await _api.PutAsync($"/events/{Id(draft)}/info", new
        {
            version = draft.GetProperty("version").GetString(),
            title = "Tên mới",
            description = "Đoạn một.\n\nĐoạn hai.",
            lineup = new[] { "Ca sĩ A", "  ", "Ca sĩ B" },
        });

        var updated = await OrganizerApi.BodyAsync(response);
        updated.GetProperty("title").GetString().ShouldBe("Tên mới");
        updated.GetProperty("lineup").GetArrayLength().ShouldBe(2);
        updated.GetProperty("version").GetString().ShouldNotBe(draft.GetProperty("version").GetString());
        updated.GetProperty("slug").GetString().ShouldBe(draft.GetProperty("slug").GetString());
    }

    [Fact]
    public async Task Update_info_on_published_event_is_allowed()
    {
        var draft = await _api.CreateDraftAsync();
        await PublishDirectlyAsync(Id(draft));
        var current = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(draft)}"));

        var response = await _api.PutAsync($"/events/{Id(draft)}/info", new { version = current.GetProperty("version").GetString(), title = "Đổi tên sau khi bán", description = "", lineup = Array.Empty<string>() });

        (await OrganizerApi.BodyAsync(response)).GetProperty("title").GetString().ShouldBe("Đổi tên sau khi bán");
    }

    [Fact]
    public async Task Update_schedule_on_published_event_returns_not_editable()
    {
        var draft = await _api.CreateDraftAsync();
        await PublishDirectlyAsync(Id(draft));
        var current = await OrganizerApi.BodyAsync(await _api.GetAsync($"/events/{Id(draft)}"));

        var response = await _api.PutAsync($"/events/{Id(draft)}/schedule", new
        {
            version = current.GetProperty("version").GetString(),
            category = "concert",
            venueId = current.GetProperty("venueId").GetGuid(),
            startsAt = OrganizerApi.Start.AddDays(1),
            endsAt = OrganizerApi.Start.AddDays(1).AddHours(2),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("event.not_editable");
    }

    [Fact]
    public async Task Moving_the_start_before_a_phase_closes_returns_phase_after_event_start()
    {
        var draft = await _api.CreateDraftAsync();
        var withPhase = await OrganizerApi.BodyAsync(await _api.PutAsync($"/events/{Id(draft)}/phases", new
        {
            version = draft.GetProperty("version").GetString(),
            phases = new object[]
            {
                new { name = "Mở bán", opensAt = DateTimeOffset.UtcNow.AddDays(1), closesAt = OrganizerApi.Start.AddDays(-1), queueEnabled = true, maxTicketsPerUser = 4 },
            },
        }));

        var response = await _api.PutAsync($"/events/{Id(draft)}/schedule", new
        {
            version = withPhase.GetProperty("version").GetString(),
            category = "concert",
            venueId = draft.GetProperty("venueId").GetGuid(),
            startsAt = OrganizerApi.Start.AddDays(-2),
            endsAt = OrganizerApi.Start.AddDays(-2).AddHours(2),
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("phase.after_event_start");
    }

    [Fact]
    public async Task Update_schedule_on_draft_moves_the_event()
    {
        var draft = await _api.CreateDraftAsync();
        var hue = Id(await _api.VenueAsync("Nhà hát Huế"));

        var response = await _api.PutAsync($"/events/{Id(draft)}/schedule", new
        {
            version = draft.GetProperty("version").GetString(),
            category = "theater",
            venueId = hue,
            startsAt = OrganizerApi.Start.AddDays(2),
            endsAt = OrganizerApi.Start.AddDays(2).AddHours(2),
        });

        var updated = await OrganizerApi.BodyAsync(response);
        updated.GetProperty("category").GetString().ShouldBe("theater");
        updated.GetProperty("venueId").GetGuid().ShouldBe(hue);
    }

    [Theory]
    [InlineData("https://evil.example.com/cover.jpg")]
    [InlineData("http://localhost:3000/media/events/00000000000000000000000000000000/x.jpg")]
    [InlineData("http://localhost:3000/media/avatars/x.jpg")]
    public async Task Cover_url_outside_event_prefix_is_rejected(string url)
    {
        var draft = await _api.CreateDraftAsync();

        var response = await _api.PutAsync($"/events/{Id(draft)}/info", new { version = draft.GetProperty("version").GetString(), title = "Tên", description = "", lineup = Array.Empty<string>(), coverImageUrl = url });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await OrganizerApi.CodeAsync(response)).ShouldBe("event.cover_not_owned");
    }

    [Fact]
    public async Task Cover_uploaded_through_the_presigned_url_is_accepted()
    {
        var draft = await _api.CreateDraftAsync();
        var ticket = await OrganizerApi.BodyAsync(await _api.PostAsync($"/events/{Id(draft)}/cover-upload-url", new { contentType = "image/jpeg", size = 1000 }));
        var publicUrl = ticket.GetProperty("publicUrl").GetString()!;

        var response = await _api.PutAsync($"/events/{Id(draft)}/info", new { version = draft.GetProperty("version").GetString(), title = "Tên", description = "", lineup = Array.Empty<string>(), coverImageUrl = publicUrl });

        (await OrganizerApi.BodyAsync(response)).GetProperty("coverImageUrl").GetString().ShouldBe(publicUrl);
        publicUrl.ShouldContain($"/events/{Id(draft):N}/");
    }

    [Theory]
    [InlineData("image/jpeg", 5 * 1024 * 1024 + 1)]
    [InlineData("image/svg+xml", 1000)]
    public async Task Cover_upload_url_rejects_over_5MB_and_svg(string contentType, long size)
    {
        var draft = await _api.CreateDraftAsync();

        var response = await _api.PostAsync($"/events/{Id(draft)}/cover-upload-url", new { contentType, size });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Stale_version_returns_concurrent_edit()
    {
        var draft = await _api.CreateDraftAsync();
        var version = draft.GetProperty("version").GetString();
        (await _api.PutAsync($"/events/{Id(draft)}/info", new { version, title = "Tab một", description = "", lineup = Array.Empty<string>() }))
            .EnsureSuccessStatusCode();

        var stale = await _api.PutAsync($"/events/{Id(draft)}/info", new { version, title = "Tab hai", description = "", lineup = Array.Empty<string>() });

        stale.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await OrganizerApi.CodeAsync(stale)).ShouldBe("event.concurrent_edit");
    }

    [Fact]
    public async Task List_searches_without_diacritics_and_filters_by_status()
    {
        var mine = await _api.CreateDraftAsync("Đêm nhạc Sơn Tùng");
        await _api.CreateDraftAsync("Hội thảo khởi nghiệp");

        var found = (await OrganizerApi.BodyAsync(await _api.GetAsync("/events?q=son%20tung"))).EnumerateArray().ToList();
        var published = (await OrganizerApi.BodyAsync(await _api.GetAsync("/events?status=Published"))).EnumerateArray().ToList();

        found.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(Id(mine));
        found[0].GetProperty("venueName").GetString().ShouldBe("Nhà hát Thử");
        published.ShouldBeEmpty();
    }

    private async Task PublishDirectlyAsync(Guid id)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.Events.SingleAsync(e => e.Id == id, TestContext.Current.CancellationToken);
        evt.Publish();
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
