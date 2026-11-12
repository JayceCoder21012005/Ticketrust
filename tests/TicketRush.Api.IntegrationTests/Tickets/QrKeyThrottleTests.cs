using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Infrastructure.Redis;
using TicketRush.Infrastructure.Tickets;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary>The Redis fixed-window limiter behind <c>qr-key</c> (one Lua script: INCR + EXPIRE) against a real Redis.</summary>
public sealed class QrKeyThrottleTests(ContainersFixture containers) : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private RedisKeys _keys = null!;
    private RedisQrKeyThrottle _throttle = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        _keys = new RedisKeys(Options.Create(new RedisOptions { Environment = $"t{Guid.NewGuid():N}"[..12] }));
        _throttle = new RedisQrKeyThrottle(_redis, _keys);
    }

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync();

    [Fact]
    public async Task The_first_ten_requests_of_a_window_are_allowed_and_the_eleventh_is_not()
    {
        var user = Guid.NewGuid();
        for (var i = 1; i <= RedisQrKeyThrottle.Limit; i++)
        {
            (await _throttle.TryAcquireAsync(user, Ct)).ShouldBe(new ThrottleResult(true, TimeSpan.Zero), $"request {i}");
        }

        var refused = await _throttle.TryAcquireAsync(user, Ct);

        refused.Allowed.ShouldBeFalse();
        refused.RetryAfter.ShouldBeGreaterThan(TimeSpan.Zero);
        refused.RetryAfter.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
        (await _throttle.TryAcquireAsync(user, Ct)).Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Users_have_separate_windows()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        for (var i = 0; i < RedisQrKeyThrottle.Limit + 1; i++)
        {
            await _throttle.TryAcquireAsync(first, Ct);
        }

        (await _throttle.TryAcquireAsync(second, Ct)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task The_counter_lives_under_the_prefix_and_always_expires_within_the_window()
    {
        var user = Guid.NewGuid();

        await _throttle.TryAcquireAsync(user, Ct);
        await _throttle.TryAcquireAsync(user, Ct);

        var key = _keys.QrKeyThrottle(user);
        key.ToString().ShouldStartWith(_keys.Prefix);
        var db = _redis.GetDatabase();
        (await db.StringGetAsync(key)).ToString().ShouldBe("2");
        var ttl = (await db.KeyTimeToLiveAsync(key))!.Value;
        ttl.ShouldBeGreaterThan(TimeSpan.FromSeconds(50));
        ttl.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task The_window_does_not_slide_when_a_refused_request_comes_in()
    {
        var user = Guid.NewGuid();
        for (var i = 0; i < RedisQrKeyThrottle.Limit; i++)
        {
            await _throttle.TryAcquireAsync(user, Ct);
        }

        var db = _redis.GetDatabase();
        await db.KeyExpireAsync(_keys.QrKeyThrottle(user), TimeSpan.FromSeconds(7));
        await _throttle.TryAcquireAsync(user, Ct);

        (await db.KeyTimeToLiveAsync(_keys.QrKeyThrottle(user)))!.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_new_window_starts_when_the_counter_expires()
    {
        var user = Guid.NewGuid();
        for (var i = 0; i < RedisQrKeyThrottle.Limit + 1; i++)
        {
            await _throttle.TryAcquireAsync(user, Ct);
        }

        await _redis.GetDatabase().KeyExpireAsync(_keys.QrKeyThrottle(user), TimeSpan.FromMilliseconds(50));
        await Task.Delay(150, Ct);

        (await _throttle.TryAcquireAsync(user, Ct)).Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Concurrent_requests_never_let_more_than_the_limit_through()
    {
        var user = Guid.NewGuid();

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => _throttle.TryAcquireAsync(user, Ct)));

        results.Count(r => r.Allowed).ShouldBe(RedisQrKeyThrottle.Limit);
    }

    [Fact]
    public async Task An_unreachable_redis_throws_ThrottleUnavailableException()
    {
        await using var dead = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=300,asyncTimeout=300");
        var throttle = new RedisQrKeyThrottle(dead, _keys);

        await Should.ThrowAsync<ThrottleUnavailableException>(() => throttle.TryAcquireAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public void The_key_is_documented_under_the_environment_prefix()
    {
        var user = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var keys = new RedisKeys(Options.Create(new RedisOptions { Environment = "x" }));

        keys.QrKeyThrottle(user).ToString().ShouldBe("tr:x:qr-key:11111111222233334444555555555555");
    }
}
