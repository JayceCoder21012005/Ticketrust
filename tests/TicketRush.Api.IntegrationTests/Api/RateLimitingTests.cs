using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Api;

/// <summary>
/// Spec 12's Redis sliding-window limiter through the real pipeline. Two hosts share one Redis (and one database), like two api
/// instances behind the load balancer: a limit holds across both. Each test gets its own Redis key prefix, so windows never leak.
/// </summary>
public sealed class RateLimitingTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _first = null!;
    private ApiFactory _second = null!;
    private HttpClient _a = null!;
    private HttpClient _b = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var database = await containers.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?>
        {
            ["RateLimits:Enabled"] = "true",
            ["Redis:Environment"] = $"rl{Guid.NewGuid():N}"[..12],
            ["RateLimits:Global:Permit"] = "6",
            ["RateLimits:Global:Window"] = "00:01:00",
            ["RateLimits:Auth:Permit"] = "3",
            ["RateLimits:Auth:Window"] = "00:15:00",
        };
        _first = new ApiFactory(database, containers.RedisConnectionString, settings: settings);
        _second = new ApiFactory(database, containers.RedisConnectionString, settings: settings);
        _a = _first.CreateClient();
        _b = _second.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _a.Dispose();
        _b.Dispose();
        await _first.DisposeAsync();
        await _second.DisposeAsync();
    }

    [Fact]
    public async Task The_global_limit_holds_across_two_instances_and_answers_429_with_retry_after()
    {
        for (var i = 0; i < 6; i++)
        {
            var client = i % 2 == 0 ? _a : _b;
            (await client.GetAsync("/api/v1/time", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"request {i + 1}");
        }

        var refused = await _b.GetAsync("/api/v1/time", Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var retryAfter = int.Parse(refused.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture);
        retryAfter.ShouldBeInRange(1, 60);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("code").GetString().ShouldBe("rate_limited");
        body.GetProperty("retryAfterSeconds").GetInt32().ShouldBe(retryAfter);
        (await _a.GetAsync("/api/v1/time", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the other instance agrees");
    }

    [Fact]
    public async Task Auth_is_limited_per_email_across_instances_and_another_email_still_gets_through()
    {
        for (var i = 0; i < 3; i++)
        {
            var client = i % 2 == 0 ? _a : _b;
            (await LoginAsync(client, "Target@Example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"attempt {i + 1}");
        }

        var refused = await LoginAsync(_a, " target@example.com ");
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, "the same address, however it is written");
        (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("rate_limited");

        (await LoginAsync(_b, "someone-else@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a different address has its own window");
    }

    [Fact]
    public async Task A_refused_request_never_reaches_the_endpoint()
    {
        for (var i = 0; i < 3; i++)
        {
            await LoginAsync(_a, "flood@example.com");
        }

        var register = await _a.PostAsJsonAsync("/api/v1/auth/register", new { email = "flood@example.com", password = "Sup3r-secret!pw", fullName = "Flood", captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }, Ct);

        register.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        await _first.DrainOutboxAsync(Ct);
        _first.Emails.Sent.ShouldNotContain(m => m.To == "flood@example.com");
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "wrong-password-123" }, Ct);
}
