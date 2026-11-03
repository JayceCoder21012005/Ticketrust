using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Catalog;

/// <summary>Spec 04 public catalog against the dev seed (15 events: every status, category and city).</summary>
public sealed class CatalogApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient();
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("son tung")]
    [InlineData("Sơn Tùng")]
    [InlineData("SON TUNG mtp")]
    [InlineData("son tunng")] // typo: trigram fallback
    public async Task Search_ignores_accents_case_and_small_typos(string q)
    {
        var page = await ListAsync($"q={Uri.EscapeDataString(q)}");

        page.Slugs().ShouldContain("son-tung-mtp-live");
    }

    [Fact]
    public async Task Search_matches_lineup_and_city()
    {
        (await ListAsync("q=ha+anh+tuan")).Slugs().ShouldBe([SeedEventCatalog.OpeningSoonSlug]);
        (await ListAsync("q=da+nang")).Slugs().ShouldBe(["workshop-thiet-ke-trai-nghiem", "my-tam-tri-ky", "marathon-da-nang-2026"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("q=%27%26%7C%21")] // '&|!
    [InlineData("q=%20%20")]
    public async Task Odd_search_text_never_fails(string query)
    {
        var response = await _client.GetAsync($"/api/v1/events?{query}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Listing_shows_only_published_events_that_have_not_ended_in_start_order()
    {
        var page = await ListAsync("limit=50");

        page.Slugs().ShouldNotContain("rock-storm-2026"); // draft
        page.Slugs().ShouldNotContain("monsoon-festival-2026"); // cancelled
        page.Slugs().ShouldNotContain("jazz-night-saigon"); // ended
        page.Slugs().Count(slug => !slug.StartsWith("cung-gio-", StringComparison.Ordinal)).ShouldBe(12);
        page.Items.Select(i => i.GetProperty("startsAt").GetDateTimeOffset()).ShouldBeInOrder();
        page.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Listing_card_has_venue_city_cheapest_price_and_sale_state()
    {
        var card = (await ListAsync("q=son+tung")).Items.Single();

        card.GetProperty("venueName").GetString().ShouldBe("Sân vận động Mỹ Đình");
        card.GetProperty("city").GetString().ShouldBe("Hà Nội");
        card.GetProperty("category").GetString().ShouldBe("concert");
        card.GetProperty("priceFrom").GetInt64().ShouldBe(800_000);
        card.GetProperty("isOnSale").GetBoolean().ShouldBeTrue();
        card.GetProperty("isSoldOut").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Upcoming_sale_card_reports_when_the_next_phase_opens()
    {
        var card = (await ListAsync("q=den+vau")).Items.Single();

        card.GetProperty("isOnSale").GetBoolean().ShouldBeFalse();
        card.GetProperty("nextSaleOpensAt").GetDateTimeOffset().ShouldBe(DateTimeOffset.UtcNow.AddDays(14), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Filters_by_city_category_price_and_dates()
    {
        (await ListAsync("city=%C4%90%C3%A0+N%E1%BA%B5ng")).Items.ShouldAllBe(i => i.GetProperty("city").GetString() == "Đà Nẵng");
        (await ListAsync("category=sports")).Slugs().ShouldBe(["v-league-ha-noi-vs-cong-an", "marathon-da-nang-2026"]);
        (await ListAsync("priceMax=250000")).Slugs().ShouldBe(["v-league-ha-noi-vs-cong-an", "saigon-teu-cuoi-xuyen-dem"]);

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(20).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(31).ToString("O"));
        (await ListAsync($"from={from}&to={to}")).Slugs().ShouldBe(["my-tam-tri-ky", "ballet-ho-thien-nga", "son-tung-mtp-live"]);
    }

    [Fact]
    public async Task Keyset_pages_neither_repeat_nor_skip_events_that_start_at_the_same_time()
    {
        await AddEventsStartingTogetherAsync(5);
        var all = (await ListAsync("limit=50")).Slugs();

        var paged = new List<string>();
        string? cursor = null;
        do
        {
            var page = await ListAsync($"limit=4{(cursor is null ? "" : $"&cursor={cursor}")}");
            paged.AddRange(page.Slugs());
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        paged.ShouldBe(all);
        all.Count.ShouldBe(17);
    }

    [Theory]
    [InlineData("cursor=zzz", "Cursor")]
    [InlineData("category=karaoke", "Category")]
    [InlineData("priceMax=-1", "PriceMax")]
    [InlineData("limit=500", "Limit")]
    public async Task Invalid_filters_are_a_validation_problem(string query, string field)
    {
        var response = await _client.GetAsync($"/api/v1/events?{query}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("errors").TryGetProperty(char.ToLowerInvariant(field[0]) + field[1..], out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Detail_has_venue_tiers_by_price_and_phases_without_access_codes()
    {
        var response = await _client.GetAsync("/api/v1/events/den-vau-show-2026", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var body = JsonDocument.Parse(raw).RootElement;

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.GetProperty("venue").GetProperty("name").GetString().ShouldBe("Nhà thi đấu Phú Thọ");
        body.GetProperty("tiers").EnumerateArray().Select(t => t.GetProperty("price").GetInt64()).ShouldBe([700_000, 2_000_000]);
        body.GetProperty("tiers")[0].TryGetProperty("remaining", out _).ShouldBeFalse();
        var phases = body.GetProperty("phases").EnumerateArray().ToList();
        phases.Select(p => p.GetProperty("name").GetString()).ShouldBe(["Presale", "Mở bán"]);
        phases[0].GetProperty("requiresAccessCode").GetBoolean().ShouldBeTrue();
        raw.ShouldNotContain("seed-presale");
        raw.ShouldNotContain("accessCodeHash", Case.Insensitive);
        body.GetProperty("lineup").EnumerateArray().Select(a => a.GetString()).ShouldBe(["Đen Vâu", "Hậu Vi", "Linh Cáo"]);
    }

    [Theory]
    [InlineData("rock-storm-2026")] // draft
    [InlineData("monsoon-festival-2026")] // cancelled
    [InlineData("no-such-event")]
    public async Task Unpublished_and_unknown_events_are_not_found(string slug)
    {
        var response = await _client.GetAsync($"/api/v1/events/{slug}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("event.not_found");
    }

    [Fact]
    public async Task Ended_events_keep_their_page_so_old_links_still_work()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>("/api/v1/events/jazz-night-saigon", Ct);

        body.GetProperty("status").GetString().ShouldBe("Ended");
    }

    [Fact]
    public async Task Seat_map_is_publicly_cacheable_and_revalidates_with_304()
    {
        var id = await EventIdAsync("son-tung-mtp-live");

        var first = await _client.GetAsync($"/api/v1/events/{id}/seat-map", Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.CacheControl!.Public.ShouldBeTrue();
        first.Headers.CacheControl.MaxAge.ShouldBe(TimeSpan.FromHours(1));
        var etag = first.Headers.ETag.ShouldNotBeNull();
        var body = await first.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("seats").GetArrayLength().ShouldBe(SeedEventCatalog.SeatsPerSeatedEvent);
        body.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("name").GetString()).ShouldBe(["A", "B", "C"]);
        var seat = body.GetProperty("seats")[0];
        seat.GetProperty("index").GetInt32().ShouldBe(0);
        seat.GetProperty("section").GetString().ShouldBe("A");

        using var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/events/{id}/seat-map");
        conditional.Headers.IfNoneMatch.Add(etag);
        var second = await _client.SendAsync(conditional, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        second.Headers.ETag.ShouldBe(etag);
        (await second.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Seat_map_of_an_unknown_event_is_not_found()
    {
        (await _client.GetAsync($"/api/v1/events/{Guid.NewGuid()}/seat-map", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Availability_reports_everything_available_until_holds_exist()
    {
        var id = await EventIdAsync("son-tung-mtp-live");

        var body = await _client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{id}/availability", Ct);

        body.GetProperty("serverNow").GetDateTimeOffset().ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        body.GetProperty("unavailable").GetArrayLength().ShouldBe(0);
        body.GetProperty("tiers").EnumerateArray().Select(t => t.GetProperty("remaining").GetInt32()).ShouldBe([200, 400, 2000], ignoreOrder: true);
    }

    [Fact]
    public async Task Categories_and_cities_are_listed()
    {
        var categories = await _client.GetFromJsonAsync<JsonElement>("/api/v1/categories", Ct);
        var cities = await _client.GetFromJsonAsync<JsonElement>("/api/v1/cities", Ct);

        categories.EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ShouldBe(EventCategories.All.Select(c => c.Slug));
        categories[0].GetProperty("name").GetString().ShouldBe("Nhạc sống");
        cities.EnumerateArray().Select(c => c.GetString()).ShouldBe(["Đà Nẵng", "Hà Nội", "Hồ Chí Minh"]);
    }

    [Fact]
    public async Task A_thousand_concurrent_requests_for_one_uncached_event_run_one_database_query()
    {
        _factory.Queries.Reset();

        var responses = await Task.WhenAll(Enumerable.Range(0, 1000)
            .Select(_ => _client.GetAsync("/api/v1/events/ballet-ho-thien-nga", Ct)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        var eventQueries = _factory.Queries.Matching(sql => sql.Contains("FROM events", StringComparison.Ordinal));
        eventQueries.Count.ShouldBe(1, string.Join("\n---\n", eventQueries));
    }

    private async Task<Guid> EventIdAsync(string slug) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/v1/events/{slug}", Ct)).GetProperty("id").GetGuid();

    private async Task<Page> ListAsync(string query)
    {
        var response = await _client.GetAsync($"/api/v1/events?{query}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var cursor = body.GetProperty("nextCursor");
        return new Page(body.GetProperty("items").EnumerateArray().ToList(), cursor.ValueKind == JsonValueKind.Null ? null : cursor.GetString());
    }

    private async Task AddEventsStartingTogetherAsync(int count)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = Venue.Create("Nhà văn hoá Thanh Niên", "4 Phạm Ngọc Thạch", "Hồ Chí Minh", null, 1000);
        db.Venues.Add(venue);
        // Far enough out not to land in the date-window, price or count assertions of other tests in this class.
        var startsAt = DateTimeOffset.UtcNow.AddDays(100);
        for (var i = 0; i < count; i++)
        {
            var evt = Event.CreateDraft(Guid.CreateVersion7(), $"cung-gio-{i}", $"Cùng giờ {i}", "workshop", venue, startsAt, startsAt.AddHours(2));
            evt.AddTier("GA", new Money(300_000), TicketKind.GeneralAdmission, 100, "#16a34a");
            evt.Publish();
            db.Events.Add(evt);
        }

        await db.SaveChangesAsync(Ct);
    }

    private sealed record Page(List<JsonElement> Items, string? NextCursor)
    {
        public List<string> Slugs() => Items.Select(i => i.GetProperty("slug").GetString()!).ToList();
    }
}
