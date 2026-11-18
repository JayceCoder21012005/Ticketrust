using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Catalog;

/// <summary>UI refresh: the home's sections, picked on the server from the dev seed.</summary>
public sealed class HomeApiTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task An_empty_catalog_returns_empty_lists_not_an_error()
    {
        var home = await HomeAsync();

        foreach (var section in new[] { "hot", "openingSoon", "thisWeek", "upcoming", "cities", "categories" })
        {
            home.GetProperty(section).GetArrayLength().ShouldBe(0, section);
        }
    }

    [Fact]
    public async Task Hot_only_holds_events_on_sale_or_opening_within_7_days_and_never_sold_out()
    {
        await SeedAsync();

        var hot = Items(await HomeAsync(), "hot");

        hot.ShouldNotBeEmpty();
        hot.Count.ShouldBeLessThanOrEqualTo(4);
        var soon = DateTimeOffset.UtcNow.AddDays(7);
        hot.ShouldAllBe(e => !e.GetProperty("isSoldOut").GetBoolean()
            && (e.GetProperty("isOnSale").GetBoolean()
                || (e.GetProperty("nextSaleOpensAt").ValueKind != JsonValueKind.Null && e.GetProperty("nextSaleOpensAt").GetDateTimeOffset() <= soon)));
    }

    [Fact]
    public async Task Followers_lift_an_event_to_the_top_of_hot()
    {
        await SeedAsync();
        var before = Items(await HomeAsync(), "hot").Select(Slug).ToList();
        var last = before[^1];
        await FollowAsync(last, followers: 30);

        var after = Items(await HomeAsync(), "hot").Select(Slug).ToList();

        after[0].ShouldBe(last);
    }

    [Fact]
    public async Task This_week_holds_events_starting_in_the_next_7_days_ordered_by_start()
    {
        await SeedAsync();

        var week = Items(await HomeAsync(), "thisWeek");

        week.Count.ShouldBeLessThanOrEqualTo(6);
        var end = DateTimeOffset.UtcNow.AddDays(7);
        week.ShouldAllBe(e => e.GetProperty("startsAt").GetDateTimeOffset() <= end);
        week.Select(e => e.GetProperty("startsAt").GetDateTimeOffset()).ShouldBeInOrder();
    }

    [Fact]
    public async Task Opening_soon_lists_sales_not_open_yet_by_opening_time()
    {
        await SeedAsync();

        var opening = Items(await HomeAsync(), "openingSoon");

        opening.ShouldContain(e => Slug(e) == SeedEventCatalog.OpeningSoonSlug);
        opening.ShouldAllBe(e => !e.GetProperty("isOnSale").GetBoolean() && !e.GetProperty("isSoldOut").GetBoolean()
            && e.GetProperty("nextSaleOpensAt").ValueKind != JsonValueKind.Null);
        opening.Select(e => e.GetProperty("nextSaleOpensAt").GetDateTimeOffset()).ShouldBeInOrder();
    }

    [Fact]
    public async Task Cities_and_categories_count_the_published_upcoming_events()
    {
        await SeedAsync();
        var listing = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/events?limit=50", Ct)).GetProperty("items").EnumerateArray().ToList();

        var home = await HomeAsync();

        var cities = home.GetProperty("cities").EnumerateArray().ToDictionary(c => c.GetProperty("city").GetString()!, c => c.GetProperty("events").GetInt32());
        cities.ShouldBe(listing.GroupBy(e => e.GetProperty("city").GetString()!).ToDictionary(g => g.Key, g => g.Count()), ignoreOrder: true);
        var categories = home.GetProperty("categories").EnumerateArray().ToDictionary(c => c.GetProperty("category").GetString()!, c => c.GetProperty("events").GetInt32());
        categories.ShouldBe(listing.GroupBy(e => e.GetProperty("category").GetString()!).ToDictionary(g => g.Key, g => g.Count()), ignoreOrder: true);
    }

    private async Task SeedAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
    }

    private async Task FollowAsync(string slug, int followers)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var eventId = db.Events.Single(e => e.Slug == slug).Id;
        for (var i = 0; i < followers; i++)
        {
            db.EventFollows.Add(EventFollow.Create(Guid.CreateVersion7(), eventId, DateTimeOffset.UtcNow));
        }

        await db.SaveChangesAsync(Ct);
        // The home is cached for 30 s, like the listing: start from a cold cache.
        await _factory.Services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>().RemoveByTagAsync("events:list", Ct);
    }

    private async Task<JsonElement> HomeAsync()
    {
        var response = await _client.GetAsync("/api/v1/catalog/home", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=30");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<JsonElement> Items(JsonElement home, string section) => home.GetProperty(section).EnumerateArray().ToList();

    private static string Slug(JsonElement e) => e.GetProperty("slug").GetString()!;
}
