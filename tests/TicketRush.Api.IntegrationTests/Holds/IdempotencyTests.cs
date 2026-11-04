using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Idempotency-Key filter (spec 00 / 07, C2), IETF draft semantics: 400 missing, 422 reused, 409 in progress.</summary>
[Collection(nameof(IdempotencyTests))]
public sealed class IdempotencyTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _auth = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        IdempotencyProbe.Reset();
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        IdempotencyProbe.Reset();
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<string> NewUserAsync() =>
        (await _auth.SignInWithRoleAsync($"idem-{Guid.NewGuid():N}@example.com", "Customer")).AccessToken;

    private Task<HttpResponseMessage> PostAsync(string token, string? key, string value = "a")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/_test/idempotency") { Content = JsonContent.Create(new { value }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return _client.SendAsync(request, Ct);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-uuid")]
    public async Task Missing_or_malformed_key_is_400_key_required(string? key)
    {
        var response = await PostAsync(await NewUserAsync(), key);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("idempotency.key_required");
        IdempotencyProbe.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task Same_key_and_body_replays_the_first_response_and_runs_once()
    {
        var token = await NewUserAsync();
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(token, key);
        var second = await PostAsync(token, key);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        second.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");
        IdempotencyProbe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task Same_key_with_another_body_is_422_key_reused()
    {
        var token = await NewUserAsync();
        var key = Guid.NewGuid().ToString();
        await PostAsync(token, key, "a");

        var response = await PostAsync(token, key, "b");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await CodeOf(response)).ShouldBe("idempotency.key_reused");
        IdempotencyProbe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_while_the_first_runs_is_409_in_progress()
    {
        var token = await NewUserAsync();
        var key = Guid.NewGuid().ToString();
        IdempotencyProbe.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = PostAsync(token, key);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (IdempotencyProbe.Runs == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        var retry = await PostAsync(token, key);
        IdempotencyProbe.Gate.SetResult();

        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(retry)).ShouldBe("idempotency.in_progress");
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
        IdempotencyProbe.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        var key = Guid.NewGuid().ToString();
        await PostAsync(await NewUserAsync(), key);

        var other = await PostAsync(await NewUserAsync(), key);

        other.StatusCode.ShouldBe(HttpStatusCode.OK);
        IdempotencyProbe.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task A_server_failure_is_not_stored_so_the_retry_runs()
    {
        var token = await NewUserAsync();
        var key = Guid.NewGuid().ToString();
        IdempotencyProbe.Fail = true;
        (await PostAsync(token, key)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        IdempotencyProbe.Fail = false;

        var retry = await PostAsync(token, key);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        IdempotencyProbe.Runs.ShouldBe(2);
    }
}
