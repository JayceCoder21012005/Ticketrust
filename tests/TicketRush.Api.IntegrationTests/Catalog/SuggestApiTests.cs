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

    private async Task<JsonElement> SuggestAsync(string q)
    {
        var response = await _client.GetAsync($"/api/v1/search/suggest?q={Uri.EscapeDataString(q)}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        response.Headers.CacheControl!.ToString().ShouldBe("public, max-age=30");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<string> Names(JsonElement suggestions, string group, string property) =>
        suggestions.GetProperty(group).EnumerateArray().Select(v => v.GetProperty(property).GetString()!).ToList();
}
