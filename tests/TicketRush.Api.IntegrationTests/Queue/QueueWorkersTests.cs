extern alias WorkerAssembly;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Locking;
using TicketRush.Infrastructure.Queue;
using WorkerAssembly::TicketRush.Worker.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class QueueWorkersTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedPhaseSource(params SaleWindow[] sales) : IQueuePhaseSource
    {
        public Task<IReadOnlyList<SaleWindow>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SaleWindow>>(sales);
    }

    private static LiveSales LiveFor(QueueRoomHarness h, params SaleWindow[] sales) =>
        new(new FixedPhaseSource(sales), Options.Create(h.Options), h.Time);

    private static RedisDistributedLock LockFor(QueueRoomHarness h) =>
        new(h.Redis, h.Keys, NullLogger<RedisDistributedLock>.Instance);

    private static QueueOpenerService OpenerFor(QueueRoomHarness h, LiveSales live, TimeProvider? time = null) =>
        new(live, new WaitingRoomOpener(h.Redis, h.Keys), LockFor(h), time ?? h.Time, NullLogger<QueueOpenerService>.Instance);

    private static QueueAdmitterService AdmitterFor(QueueRoomHarness h, LiveSales live, TimeProvider? time = null) =>
        AdmitterFor(h, live, new RecordingSalesMetrics(), time);

    private static QueueAdmitterService AdmitterFor(QueueRoomHarness h, LiveSales live, RecordingSalesMetrics metrics, TimeProvider? time = null) =>
        new(live, new WaitingRoomAdmitter(h.Redis, h.Keys, Options.Create(h.Options), time ?? h.Time), metrics, LockFor(h), time ?? h.Time, NullLogger<QueueAdmitterService>.Instance);

    [Fact]
    public async Task Opener_opens_phase_at_opensAt()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var users = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        var opener = OpenerFor(h, LiveFor(h, sale));

        await opener.RunOnceAsync(Ct);
        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "status")).ToString().ShouldBe("Pre");

        h.Time.Advance(TimeSpan.FromMinutes(10));
        await opener.RunOnceAsync(Ct);

        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "status")).ToString().ShouldBe("Open");
        (await h.Db.HashLengthAsync(h.Keys.QueueLine(sale.PhaseId))).ShouldBe(20);
    }

    [Fact]
    public async Task Admitter_ticks_while_open_and_closes_phase_at_closesAt()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1), lasts: TimeSpan.FromHours(1));
        await h.Room.JoinAsync(sale, Guid.NewGuid(), Ct);
        var metrics = new RecordingSalesMetrics();
        var admitter = AdmitterFor(h, LiveFor(h, sale), metrics);

        await admitter.RunOnceAsync(Ct);
        ((long)await h.Db.StringGetAsync(h.Keys.QueueAdmitted(sale.PhaseId))).ShouldBe(1);
        metrics.AdmittedTotal.ShouldBe(1);
        // Nobody waits any more; the admitted buyer counts as shopping once they claim their turn.
        metrics.Queues[sale.PhaseId].ShouldBe((0L, 0L));

        h.Time.Advance(TimeSpan.FromHours(2));
        await admitter.RunOnceAsync(Ct);

        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "status")).ToString().ShouldBe("Closed");
        metrics.Closed.ShouldContain(sale.PhaseId);
    }

    [Fact]
    public async Task Leader_failover_takes_over_within_10s_without_skipping_or_duplicating_numbers()
    {
        // The admitters run on the real clock (Redis TTLs are real), so the room must agree with it.
        var now = DateTimeOffset.UtcNow;
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 20; o.MaxActiveShoppers = 10_000; }, start: now);
        var sale = new SaleWindow(Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-1), now.AddHours(1), QueueEnabled: true);
        var real = TimeProvider.System;
        var live = new LiveSales(new FixedPhaseSource(sale), Options.Create(h.Options), real);
        var a = AdmitterFor(h, live, real);
        var b = AdmitterFor(h, live, real);

        // Joins keep arriving the whole time, including across the failover.
        using var stop = new CancellationTokenSource();
        var joiners = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await h.Room.JoinAsync(sale, Guid.NewGuid(), CancellationToken.None);

                    await Task.Delay(2, CancellationToken.None);
                }
            },
            CancellationToken.None);

        await a.StartAsync(Ct);
        await b.StartAsync(Ct);
        await WaitUntilAsync(() => a.TickCount + b.TickCount >= 2, TimeSpan.FromSeconds(10), "a leader is ticking");
        var (leader, follower) = a.TickCount >= b.TickCount ? (a, b) : (b, a);
        follower.TickCount.ShouldBe(0);

        // Crash: someone else owns the key (the leader's renewal fails), then the leader process is gone.
        await h.Db.StringSetAsync(h.Keys.Lock("queue-admitter"), "dead-leader", TimeSpan.FromSeconds(3));
        await leader.StopAsync(Ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var admittedAtCrash = (long)await h.Db.StringGetAsync(h.Keys.QueueAdmitted(sale.PhaseId));

        await WaitUntilAsync(() => follower.TickCount > 0, TimeSpan.FromSeconds(10), "follower took over");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(
            () => (long)h.Db.StringGet(h.Keys.QueueAdmitted(sale.PhaseId)) > admittedAtCrash + 20,
            TimeSpan.FromSeconds(10),
            $"admission continues after failover (crash={admittedAtCrash})");

        await stop.CancelAsync();
        await joiners;
        await follower.StopAsync(Ct);

        var line = await h.Db.HashGetAllAsync(h.Keys.QueueLine(sale.PhaseId));
        var seq = (long)await h.Db.StringGetAsync(h.Keys.QueueSeq(sale.PhaseId));
        var admitted = (long)await h.Db.StringGetAsync(h.Keys.QueueAdmitted(sale.PhaseId));
        line.Select(e => (long)e.Value).Order().ShouldBe(Enumerable.Range(1, (int)seq).Select(n => (long)n));
        admitted.ShouldBeLessThanOrEqualTo(seq);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not met in time: {what}");
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }
}
