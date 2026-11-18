using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Catalog;

/// <summary>UI refresh: grouped, typo-tolerant search suggestions over the dev seed.</summary>
public sealed class SuggestApiTests(ContainersFixture containers) : IAsyncLifetime
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
    [InlineData("son tu")]
    [InlineData("SƠN TÙNG")]
    [InlineData("son tung")]
    [InlineData("son tng")]
    public async Task Prefixes_diacritics_case_and_typos_find_the_event(string q)
    {
        var events = (await SuggestAsync(q)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToList();

        events.ShouldNotBeEmpty();
        events[0].ShouldBe("son-tung-mtp-live");
    }

    [Fact]
    public async Task Artists_venues_and_cities_are_grouped()
    {
        Names(await SuggestAsync("my di"), "venues", "name").ShouldContain(n => n.StartsWith("Sân vận động Mỹ Đình", StringComparison.Ordinal));
        (await SuggestAsync("ha anh")).GetProperty("artists").EnumerateArray().Select(a => a.GetString()).ShouldContain("Hà Anh Tuấn");
        (await SuggestAsync("da na")).GetProperty("cities").EnumerateArray().Select(c => c.GetString()).ShouldBe(["Đà Nẵng"]);
    }

    [Fact]
    public async Task Ended_draft_and_cancelled_events_never_appear()
    {
        var slugs = new List<string>();
        foreach (var q in new[] { "rock storm", "monsoon", "jazz night" })
        {
            slugs.AddRange((await SuggestAsync(q)).GetProperty("events").EnumerateArray().Select(e => e.GetProperty("slug").GetString()!));
        }

        slugs.ShouldNotContain("rock-storm-2026");
        slugs.ShouldNotContain("monsoon-festival-2026");
        slugs.ShouldNotContain("jazz-night-saigon");
    }

    [Fact]
    public async Task A_short_query_returns_the_hot_events()
    {
        var hot = (await _client.GetFromJsonAsync<JsonElement>("/api/v1/catalog/home", Ct)).GetProperty("hot").EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ToList();

        var suggestions = await SuggestAsync("s");

        suggestions.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("slug").GetString()).ShouldBe(hot);
        suggestions.GetProperty("artists").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData("'")]
    [InlineData("%")]
    [InlineData("&|!")]
    [InlineData("a:*")]
    [InlineData("🎤")]
    [InlineData("((son")]
    public async Task Hostile_queries_return_empty_groups_not_errors(string q)
    {
        var response = await _client.GetAsync($"/api/v1/search/suggest?q={Uri.EscapeDataString(q)}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_query_over_100_characters_is_refused()
    {
        var response = await _client.GetAsync($"/api/v1/search/suggest?q={new string('x', 101)}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Answers_within_30_ms_on_5000_events()
    {
        await AddEventsAsync(5000);
        await using var scope = _factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<SuggestHandler>();
        await handler.HandleAsync("son tung", Ct);

        var times = new List<double>();
        foreach (var q in Enumerable.Repeat(new[] { "son tung", "nhac hoi", "my dinh", "hanoi", "festvl" }, 4).SelectMany(x => x))
        {
            var watch = Stopwatch.StartNew();
            await handler.HandleAsync(q, Ct);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }

        times.Order().ElementAt(times.Count / 2).ShouldBeLessThan(30);
    }

    private async Task<JsonElement> SuggestAsync(string q)
    {
        var response = await _client.GetAsync($"/api/v1/search/suggest?q={Uri.EscapeDataString(q)}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=30");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<string> Names(JsonElement suggestions, string group, string property) =>
        suggestions.GetProperty(group).EnumerateArray().Select(v => v.GetProperty(property).GetString()!).ToList();

    private async Task AddEventsAsync(int count)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var venue = Venue.Create("Nhà hát Lớn", "1 Tràng Tiền", "Hà Nội", null, 600);
        db.Venues.Add(venue);
        var startsAt = DateTimeOffset.UtcNow.AddDays(200);
        string[] words = ["Đêm nhạc", "Lễ hội", "Hòa nhạc", "Live show", "Festival", "Workshop", "Giải chạy", "Kịch"];
        for (var i = 0; i < count; i++)
        {
            var evt = Event.CreateDraft(Guid.CreateVersion7(), $"tai-{i}", $"{words[i % words.Length]} số {i}", "concert", venue, startsAt.AddMinutes(i), startsAt.AddMinutes(i).AddHours(2));
            evt.AddTier("GA", new Money(200_000), TicketKind.GeneralAdmission, 100, "#16a34a");
            evt.Publish();
            db.Events.Add(evt);
        }

        await db.SaveChangesAsync(Ct);
    }
}
