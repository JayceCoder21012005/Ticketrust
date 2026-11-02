using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Locking;

public sealed class RedisDistributedLockTests(ContainersFixture containers) : IAsyncLifetime
{
    private ConnectionMultiplexer _redis = null!;
    private RedisDistributedLock _lock = null!;
    private RedisKeys _keys = null!;

    public async ValueTask InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        _keys = new RedisKeys(Options.Create(new RedisOptions { Environment = "locktest" }));
        _lock = new RedisDistributedLock(_redis, _keys, NullLogger<RedisDistributedLock>.Instance);
    }

    public async ValueTask DisposeAsync() => await _redis.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_one_holder_at_a_time_and_released_on_dispose()
    {
        var name = $"job-{Guid.NewGuid():N}";

        await using (var first = await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(10), Ct))
        {
            first.ShouldNotBeNull();
            (await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(10), Ct)).ShouldBeNull();
        }

        await using var second = await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(10), Ct);
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Held_lock_is_renewed_past_its_ttl()
    {
        var name = $"job-{Guid.NewGuid():N}";
        await using var handle = await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(1), Ct);

        await Task.Delay(TimeSpan.FromSeconds(2.5), Ct);

        (await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(1), Ct)).ShouldBeNull();
        handle!.LockLost.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task Losing_the_key_signals_LockLost_and_never_deletes_the_new_owners_lock()
    {
        var name = $"job-{Guid.NewGuid():N}";
        var handle = await _lock.TryAcquireAsync(name, TimeSpan.FromSeconds(1), Ct);
        await _redis.GetDatabase().StringSetAsync(_keys.Lock(name), "someone-else");

        await Task.Delay(TimeSpan.FromSeconds(1), Ct);
        handle!.LockLost.IsCancellationRequested.ShouldBeTrue();
        await handle.DisposeAsync();

        (await _redis.GetDatabase().StringGetAsync(_keys.Lock(name))).ToString().ShouldBe("someone-else");
    }
}
