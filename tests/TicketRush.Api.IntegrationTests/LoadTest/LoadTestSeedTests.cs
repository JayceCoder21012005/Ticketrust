using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.LoadTest;

/// <summary>The spec 17 fixture endpoint: only in the LoadTest environment, only with the key, and what it creates is sellable.</summary>
public sealed class LoadTestSeedTests(ContainersFixture containers)
{
    private const string Key = "load-test-key-for-the-integration-tests-0123456789";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Seed(object body, string? key = Key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/loadtest/seed") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("X-LoadTest-Key", key);
        }

        return request;
    }

    private async Task<ApiFactory> HostAsync(string environment)
    {
        var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, environment, new Dictionary<string, string?>
        {
            ["LoadTest:Key"] = Key,
        });
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        return factory;
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Outside_the_LoadTest_environment_the_endpoint_does_not_exist_even_with_the_key(string environment)
    {
        await using var factory = await HostAsync(environment);
        using var client = factory.CreateClient();

        (await client.SendAsync(Seed(new { users = 1 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Without_the_right_key_it_looks_like_no_endpoint_and_a_bad_request_is_refused()
    {
        await using var factory = await HostAsync("LoadTest");
        using var client = factory.CreateClient();

        (await client.SendAsync(Seed(new { users = 1 }, key: null), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(Seed(new { users = 1 }, key: Key + "x"), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.SendAsync(Seed(new { users = 0 }), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task It_creates_a_fresh_open_sale_with_its_seats_and_tiers_and_buyers_whose_tokens_work()
    {
        await using var factory = await HostAsync("LoadTest");
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(Seed(new { users = 3, seats = 1200, gaTiers = 2, gaCapacity = 300, hotSeats = 40, queueEnabled = false }), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        var seed = JsonDocument.Parse(text).RootElement;
        var eventId = seed.GetProperty("eventId").GetGuid();
        seed.GetProperty("gaTierIds").GetArrayLength().ShouldBe(2);
        seed.GetProperty("hotSeats").GetInt32().ShouldBe(40);
        var buyers = seed.GetProperty("buyers").EnumerateArray().ToList();
        buyers.Count.ShouldBe(3);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).Include(e => e.Phases).SingleAsync(e => e.Id == eventId, Ct);
            evt.Status.ShouldBe(EventStatus.Published);
            evt.Tiers.Count(t => t.Kind == TicketKind.GeneralAdmission && t.Capacity == 300).ShouldBe(2);
            evt.Phases.ShouldHaveSingleItem().OpensAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
            (await db.EventSeats.CountAsync(s => s.EventId == eventId, Ct)).ShouldBe(1200);
        }

        // A second run gets its own event, so runs never share inventory.
        using var again = await client.SendAsync(Seed(new { users = 1, seats = 10, gaTiers = 0, hotSeats = 0 }), Ct);
        JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("eventId").GetGuid().ShouldNotBe(eventId);

        // A pre-issued token is a signed-in buyer: no password, no login. The sale is open, so the waiting room takes them.
        using var join = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/events/{eventId}/queue/join")
        {
            Content = JsonContent.Create(new { captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }),
        };
        join.Headers.Authorization = new AuthenticationHeaderValue("Bearer", buyers[0].GetProperty("token").GetString());
        using var joined = await client.SendAsync(join, Ct);
        joined.IsSuccessStatusCode.ShouldBeTrue(await joined.Content.ReadAsStringAsync(Ct));
    }
}
