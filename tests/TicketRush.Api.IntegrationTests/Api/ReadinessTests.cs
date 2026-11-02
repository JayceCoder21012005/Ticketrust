using System.Net;
using Testcontainers.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Api;

/// <summary>Uses its own Redis container because it pauses it.</summary>
public sealed class ReadinessTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.4").Build();
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        await _redis.StartAsync();
        var database = await containers.CreateDatabaseAsync();
        _factory = new ApiFactory(database, _redis.GetConnectionString());
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Readiness_is_503_while_redis_is_down_and_200_when_it_is_back()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _client.GetAsync(new Uri("/alive", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await _redis.PauseAsync(ct);
        (await _client.GetAsync(new Uri("/alive", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await _client.GetAsync(new Uri("/health", UriKind.Relative), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await _redis.UnpauseAsync(ct);
        await WaitUntilAsync(async () => (await _client.GetAsync(new Uri("/alive", UriKind.Relative), ct)).StatusCode == HttpStatusCode.OK, ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        for (var i = 0; i < 30; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        throw new TimeoutException("Readiness did not recover within 30 s.");
    }
}
