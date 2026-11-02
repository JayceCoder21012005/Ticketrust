using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Api;

public sealed class ApiPipelineTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var database = await containers.CreateDatabaseAsync();
        _factory = new ApiFactory(database, containers.RedisConnectionString);
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Unhandled_exception_returns_problem_details_with_traceId_and_no_stack_trace()
    {
        var response = await _client.GetAsync(new Uri("/_test/throw", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("traceId").GetString().ShouldNotBeNullOrEmpty();
        json.RootElement.GetProperty("code").GetString().ShouldBe("server.error");
        body.ShouldNotContain("secret internal detail");
        body.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Result_errors_map_to_status_and_code()
    {
        var response = await _client.GetAsync(new Uri("/_test/conflict", UriKind.Relative), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        json.RootElement.GetProperty("code").GetString().ShouldBe("seat.already_held");
    }

    [Fact]
    public async Task Correlation_id_from_the_client_is_echoed()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/time");
        request.Headers.Add("X-Correlation-Id", "abc-123");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem().ShouldBe("abc-123");
    }

    [Fact]
    public async Task Unsafe_correlation_id_is_replaced_with_a_generated_one()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/time");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", "<script>alert(1)</script>");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var echoed = response.Headers.GetValues("X-Correlation-Id").ShouldHaveSingleItem();
        echoed.ShouldNotContain("<");
        echoed.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Time_endpoint_returns_server_now_in_camel_case()
    {
        var body = await _client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/time", UriKind.Relative), TestContext.Current.CancellationToken);

        body.GetProperty("serverNow").GetDateTimeOffset().ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/time", UriKind.Relative), TestContext.Current.CancellationToken);

        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldContain("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Liveness_and_readiness_are_healthy_when_dependencies_are_up()
    {
        (await _client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _client.GetAsync(new Uri("/alive", UriKind.Relative), TestContext.Current.CancellationToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
