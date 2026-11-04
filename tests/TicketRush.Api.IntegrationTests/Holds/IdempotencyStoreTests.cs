using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Idempotency;
using TicketRush.Infrastructure.Idempotency;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Holds;

/// <summary>Idempotency keys in Redis: a request that dies mid-flight must not block retries for the whole window.</summary>
public sealed class IdempotencyStoreTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_pending_marker_expires_quickly_and_the_stored_answer_keeps_the_full_window()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var keys = new RedisKeys(Options.Create(new RedisOptions { Environment = $"m{Guid.NewGuid():N}"[..12] }));
        var store = new RedisIdempotencyStore(redis, keys);
        var (user, key) = (Guid.NewGuid(), Guid.NewGuid());

        (await store.BeginAsync(user, key, "fp", TimeSpan.FromMinutes(8), Ct)).ShouldBeOfType<IdempotencyBegin.Started>();
        var pending = await redis.GetDatabase().KeyTimeToLiveAsync(keys.Idempotency(user, key));

        await store.CompleteAsync(user, key, "fp", 200, "application/json", [1], TimeSpan.FromMinutes(8), Ct);
        var stored = await redis.GetDatabase().KeyTimeToLiveAsync(keys.Idempotency(user, key));

        pending.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(RedisIdempotencyStore.PendingTtl);
        stored.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.FromMinutes(7));
    }
}
