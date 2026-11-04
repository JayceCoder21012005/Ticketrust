using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>The mock gateway's control endpoints exist only where the configuration says so.</summary>
public sealed class MockGatewayEndpointsTests(ContainersFixture containers) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly List<ApiFactory> _factories = [];
    private string _redisEnvironment = string.Empty;

    public ValueTask InitializeAsync()
    {
        _redisEnvironment = $"e{Guid.NewGuid():N}"[..12];
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in _factories)
        {
            await factory.DisposeAsync();
        }
    }

    private async Task<ApiFactory> FactoryAsync(string environment, params (string Key, string? Value)[] settings)
    {
        var all = settings.ToDictionary(s => s.Key, s => s.Value);
        all["Redis:Environment"] = _redisEnvironment;
        var factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, environment, all);
        _factories.Add(factory);
        return factory;
    }

    private static bool MockRoutesAreMapped(ApiFactory factory) =>
        factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Any(e => e.RoutePattern.RawText?.Contains("mock-gateway", StringComparison.Ordinal) == true);

    private static async Task<string> CreateSessionAsync(ApiFactory factory)
    {
        var gateway = factory.Services.GetRequiredService<IPaymentGatewayResolver>().Default;
        var session = await gateway.CreateSessionAsync(
            PaymentHarness.NewOrder(),
            new Uri("http://localhost:3000/checkout/result"),
            DateTimeOffset.UtcNow.AddMinutes(7),
            Guid.NewGuid().ToString("N"),
            Ct);
        return session.SessionId;
    }

    [Fact]
    public async Task The_endpoints_are_not_mapped_when_the_mock_is_disabled()
    {
        var factory = await FactoryAsync("Production");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/mock-gateway/sessions/mock_x", UriKind.Relative), Ct);
        var complete = await client.PostAsJsonAsync(new Uri("/api/v1/mock-gateway/sessions/mock_x/complete", UriKind.Relative), new { outcome = "succeeded" }, Ct);

        MockRoutesAreMapped(factory).ShouldBeFalse();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        complete.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("mock_gateway.");
    }

    [Fact]
    public async Task The_endpoints_are_mapped_in_Development_by_the_dev_settings()
    {
        var factory = await FactoryAsync("Development");
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/mock-gateway/sessions/mock_x", UriKind.Relative), Ct);

        MockRoutesAreMapped(factory).ShouldBeTrue();
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("mock_gateway.session_not_found");
    }

    [Fact]
    public async Task Production_with_the_mock_enabled_refuses_to_start()
    {
        var factory = await FactoryAsync("Production", ("Payments:Mock:Enabled", "true"));

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("Payments:Mock:Enabled must not be true in Production");
    }

    [Fact]
    public async Task Production_with_the_mock_enabled_starts_with_the_explicit_override()
    {
        var factory = await FactoryAsync("Production", ("Payments:Mock:Enabled", "true"), ("Payments:Mock:AllowInProduction", "true"));
        using var client = factory.CreateClient();

        MockRoutesAreMapped(factory).ShouldBeTrue();
        (await client.GetAsync(new Uri("/api/v1/mock-gateway/sessions/mock_x", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Production_without_a_webhook_secret_refuses_to_start()
    {
        var factory = await FactoryAsync("Production", ("Payments:Mock:WebhookSecret", string.Empty));

        var failure = Should.Throw<Exception>(() => factory.CreateClient());

        Flatten(failure).ShouldContain("WebhookSecret");
    }

    [Fact]
    public async Task A_session_can_be_read_and_completed_over_http()
    {
        var factory = await FactoryAsync("Development");
        using var client = factory.CreateClient();
        var id = await CreateSessionAsync(factory);

        var read = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/mock-gateway/sessions/{id}", UriKind.Relative), Ct);
        var completed = await client.PostAsJsonAsync(new Uri($"/api/v1/mock-gateway/sessions/{id}/complete", UriKind.Relative), new { outcome = "succeeded_twice" }, Ct);
        var again = await client.PostAsJsonAsync(new Uri($"/api/v1/mock-gateway/sessions/{id}/complete", UriKind.Relative), new { outcome = "failed" }, Ct);

        read.GetProperty("sessionId").GetString().ShouldBe(id);
        read.GetProperty("amount").GetInt64().ShouldBe(1_000_000);
        read.GetProperty("currency").GetString().ShouldBe("VND");
        read.GetProperty("status").GetString().ShouldBe("Pending");
        read.GetProperty("label").GetString().ShouldStartWith("Đơn hàng");
        read.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await completed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("Succeeded");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(Ct)).ShouldContain("mock_gateway.already_completed");
        (await factory.Services.GetRequiredService<MockWebhookQueue>().CountAsync()).ShouldBe(2);
        (await factory.Services.GetRequiredService<IPaymentGatewayResolver>().Default.GetStatusAsync(id, Ct)).ShouldBe(PaymentStatus.Succeeded);
    }

    [Theory]
    [InlineData("succeeded", 1)]
    [InlineData("failed", 1)]
    [InlineData("succeeded_delayed", 1)]
    [InlineData("succeeded_twice", 2)]
    public async Task Every_outcome_queues_its_webhooks(string outcome, int expectedDeliveries)
    {
        var factory = await FactoryAsync("Development");
        using var client = factory.CreateClient();
        var id = await CreateSessionAsync(factory);
        var queue = factory.Services.GetRequiredService<MockWebhookQueue>();
        var before = await queue.CountAsync();

        var response = await client.PostAsJsonAsync(new Uri($"/api/v1/mock-gateway/sessions/{id}/complete", UriKind.Relative), new { outcome }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await queue.CountAsync() - before).ShouldBe(expectedDeliveries);
    }

    [Fact]
    public async Task A_delayed_outcome_is_scored_three_minutes_ahead()
    {
        var factory = await FactoryAsync("Development");
        using var client = factory.CreateClient();
        var id = await CreateSessionAsync(factory);
        var redis = factory.Services.GetRequiredService<IConnectionMultiplexer>();
        var keys = factory.Services.GetRequiredService<RedisKeys>();

        await client.PostAsJsonAsync(new Uri($"/api/v1/mock-gateway/sessions/{id}/complete", UriKind.Relative), new { outcome = "succeeded_delayed" }, Ct);

        var scores = (await redis.GetDatabase().SortedSetRangeByRankWithScoresAsync(keys.PayMockDeliveries)).Select(e => e.Score).ToList();
        var due = DateTimeOffset.FromUnixTimeMilliseconds((long)scores.Max());
        (due - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromSeconds(170), TimeSpan.FromSeconds(181));
    }

    [Fact]
    public async Task An_unknown_outcome_or_session_is_rejected()
    {
        var factory = await FactoryAsync("Development");
        using var client = factory.CreateClient();
        var id = await CreateSessionAsync(factory);

        var bogus = await client.PostAsJsonAsync(new Uri($"/api/v1/mock-gateway/sessions/{id}/complete", UriKind.Relative), new { outcome = "bogus" }, Ct);
        var missing = await client.PostAsJsonAsync(new Uri("/api/v1/mock-gateway/sessions/mock_nope/complete", UriKind.Relative), new { outcome = "succeeded" }, Ct);

        bogus.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await factory.Services.GetRequiredService<IPaymentGatewayResolver>().Default.GetStatusAsync(id, Ct)).ShouldBe(PaymentStatus.Pending);
    }

    [Fact]
    public async Task The_registered_gateway_is_the_resilient_mock_and_the_webhook_route_can_find_it()
    {
        var factory = await FactoryAsync("Production");
        using var client = factory.CreateClient();
        var resolver = factory.Services.GetRequiredService<IPaymentGatewayResolver>();

        resolver.Default.ShouldBeOfType<ResilientPaymentGateway>();
        resolver.Get("mock").ShouldBeSameAs(resolver.Default);
        resolver.Default.Provider.ShouldBe("mock");
    }

    [Fact]
    public async Task An_outage_trips_the_breaker_and_surfaces_as_gateway_unavailable_over_the_real_registration()
    {
        var factory = await FactoryAsync(
            "Production",
            ("Payments:Resilience:RetryBaseDelay", "00:00:00"),
            ("Payments:Resilience:BreakerMinimumThroughput", "4"));
        using var client = factory.CreateClient();
        var resolver = factory.Services.GetRequiredService<IPaymentGatewayResolver>();
        factory.Services.GetRequiredService<MockPaymentGateway>().SimulateOutage = true;

        for (var i = 0; i < 3; i++)
        {
            await Should.ThrowAsync<PaymentGatewayUnavailableException>(() => resolver.Default.GetStatusAsync("mock_x", Ct));
        }
    }

    private static string Flatten(Exception exception) =>
        string.Join(" | ", Chain(exception).Select(e => e.Message));

    private static IEnumerable<Exception> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
