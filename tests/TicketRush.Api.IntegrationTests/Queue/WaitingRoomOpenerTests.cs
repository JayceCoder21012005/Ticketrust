using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class WaitingRoomOpenerTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Puts <paramref name="count"/> users into the pre-queue (one real join creates the room, the rest are bulk-added).</summary>
    private static async Task<Guid[]> FillPreQueueAsync(QueueRoomHarness h, SaleWindow sale, int count)
    {
        var users = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        await h.Room.JoinAsync(sale, users[0], Ct);
        await h.Db.SetAddAsync(h.Keys.QueuePre(sale.PhaseId), [.. users.Select(u => (RedisValue)u.ToString())]);
        return users;
    }

    private static async Task<Dictionary<Guid, long>> LineAsync(QueueRoomHarness h, SaleWindow sale) =>
        (await h.Db.HashGetAllAsync(h.Keys.QueueLine(sale.PhaseId)))
            .ToDictionary(e => Guid.Parse(e.Name.ToString()), e => (long)e.Value);

    [Fact]
    public async Task Ten_thousand_pre_users_get_unique_numbers_1_to_10000()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var users = await FillPreQueueAsync(h, sale, 10_000);

        var opened = await new WaitingRoomOpener(h.Redis, h.Keys).OpenAsync(sale.PhaseId, Ct);

        opened.ShouldBe(10_000);
        var line = await LineAsync(h, sale);
        line.Count.ShouldBe(10_000);
        line.Values.Order().ShouldBe(Enumerable.Range(1, 10_000).Select(n => (long)n));
        line.Keys.ToHashSet().SetEquals(users).ShouldBeTrue();
        (await h.Db.StringGetAsync(h.Keys.QueueSeq(sale.PhaseId))).ToString().ShouldBe("10000");
        (await h.Db.SetLengthAsync(h.Keys.QueuePre(sale.PhaseId))).ShouldBe(0);
        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "status")).ToString().ShouldBe("Open");
    }

    [Fact]
    public async Task Two_runs_produce_different_orders()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var opener = new WaitingRoomOpener(h.Redis, h.Keys);
        var saleA = h.Sale(TimeSpan.FromMinutes(10));
        var saleB = h.Sale(TimeSpan.FromMinutes(10));
        var users = await FillPreQueueAsync(h, saleA, 300);
        await h.Room.JoinAsync(saleB, users[0], Ct);
        await h.Db.SetAddAsync(h.Keys.QueuePre(saleB.PhaseId), [.. users.Select(u => (RedisValue)u.ToString())]);

        await opener.OpenAsync(saleA.PhaseId, Ct);
        await opener.OpenAsync(saleB.PhaseId, Ct);

        var (a, b) = (await LineAsync(h, saleA), await LineAsync(h, saleB));
        users.Count(u => a[u] != b[u]).ShouldBeGreaterThan(100);
    }

    [Fact]
    public async Task Open_is_idempotent_when_already_open()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        await FillPreQueueAsync(h, sale, 50);
        var opener = new WaitingRoomOpener(h.Redis, h.Keys);

        await opener.OpenAsync(sale.PhaseId, Ct);
        var before = await LineAsync(h, sale);
        var second = await opener.OpenAsync(sale.PhaseId, Ct);

        second.ShouldBe(0);
        (await LineAsync(h, sale)).ShouldBe(before);
    }

    [Fact]
    public async Task Late_join_after_open_gets_number_above_every_pre_user()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        await FillPreQueueAsync(h, sale, 100);
        await new WaitingRoomOpener(h.Redis, h.Keys).OpenAsync(sale.PhaseId, Ct);

        var late = await h.Room.JoinAsync(sale, Guid.NewGuid(), Ct);

        late.Status.ShouldBe(RoomStatus.Open);
        late.Number.ShouldBe(101);
    }

    [Fact]
    public async Task Resumes_an_interrupted_opening_without_gaps_or_duplicates()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var users = await FillPreQueueAsync(h, sale, 40);

        // A previous leader began opening and numbered 15 users, then died.
        (await QueueScripts.BeginOpenAsync(h.Db, h.Keys, sale.PhaseId)).ShouldBeTrue();
        await QueueScripts.AssignBatchAsync(h.Db, h.Keys, sale.PhaseId, [.. users.Take(15).Select(u => u.ToString())]);

        var resumed = await new WaitingRoomOpener(h.Redis, h.Keys).OpenAsync(sale.PhaseId, Ct);

        resumed.ShouldBe(25);
        (await LineAsync(h, sale)).Values.Order().ShouldBe(Enumerable.Range(1, 40).Select(n => (long)n));
    }

    [Fact]
    public async Task Late_joiner_between_begin_and_assign_is_numbered_after_every_pre_user()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var users = await FillPreQueueAsync(h, sale, 60);

        // T0 passed (Opening) but the opener has not read the pre-queue yet: this user arrived after T0.
        (await QueueScripts.BeginOpenAsync(h.Db, h.Keys, sale.PhaseId)).ShouldBeTrue();
        var late = Guid.NewGuid();
        (await h.Room.JoinAsync(sale, late, Ct)).Status.ShouldBe(RoomStatus.Opening);
        await new WaitingRoomOpener(h.Redis, h.Keys).OpenAsync(sale.PhaseId, Ct);

        var line = await LineAsync(h, sale);
        line[late].ShouldBe(61);
        users.Max(u => line[u]).ShouldBe(60);
    }

    [Fact]
    public async Task Resuming_after_a_crash_still_numbers_post_T0_joiners_after_the_whole_pre_queue()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var users = await FillPreQueueAsync(h, sale, 40);
        (await QueueScripts.BeginOpenAsync(h.Db, h.Keys, sale.PhaseId)).ShouldBeTrue();
        await QueueScripts.AssignBatchAsync(h.Db, h.Keys, sale.PhaseId, [.. users.Take(10).Select(u => u.ToString())]);

        // The leader died; during the failover gap more people arrive.
        var late = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in late)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        await new WaitingRoomOpener(h.Redis, h.Keys).OpenAsync(sale.PhaseId, Ct);

        var line = await LineAsync(h, sale);
        line.Values.Order().ShouldBe(Enumerable.Range(1, 45).Select(n => (long)n));
        users.Max(u => line[u]).ShouldBe(40);
        late.Min(u => line[u]).ShouldBe(41);
    }

    [Fact]
    public async Task Pre_queue_keys_also_expire_after_the_sale()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        await FillPreQueueAsync(h, sale, 5);
        (await QueueScripts.BeginOpenAsync(h.Db, h.Keys, sale.PhaseId)).ShouldBeTrue();
        await h.Room.JoinAsync(sale, Guid.NewGuid(), Ct); // late: lands in `pre` again

        var expected = sale.ClosesAt.AddDays(1) - DateTimeOffset.UtcNow;
        foreach (var key in new[] { h.Keys.QueuePre(sale.PhaseId), h.Keys.QueueOpening(sale.PhaseId) })
        {
            var ttl = await h.Db.KeyTimeToLiveAsync(key);
            ttl.ShouldNotBeNull($"{key} has no expiry");
            ttl.Value.ShouldBeInRange(expected - TimeSpan.FromMinutes(1), expected + TimeSpan.FromSeconds(5));
        }
    }
}
