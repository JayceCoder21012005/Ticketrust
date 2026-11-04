using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class WaitingRoomAdmitterTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WaitingRoomAdmitter AdmitterFor(QueueRoomHarness h) =>
        new(h.Redis, h.Keys, Microsoft.Extensions.Options.Options.Create(h.Options), h.Time);

    private static async Task<Guid[]> JoinAsync(QueueRoomHarness h, SaleWindow sale, int count)
    {
        var users = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        return users;
    }

    private static async Task<long> AdmittedAsync(QueueRoomHarness h, SaleWindow sale) =>
        (long)await h.Db.StringGetAsync(h.Keys.QueueAdmitted(sale.PhaseId));

    [Fact]
    public async Task Tick_admits_up_to_rate_then_stops_at_seq()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 50; o.MaxActiveShoppers = 1_000; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        await JoinAsync(h, sale, 120);
        var admitter = AdmitterFor(h);

        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(50);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(100);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(120);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(120);
    }

    [Fact]
    public async Task Tick_never_admits_beyond_seq()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var admitter = AdmitterFor(h);

        // Room exists but nobody joined: seq = 0.
        await h.Room.JoinAsync(sale, Guid.NewGuid(), Ct);
        await h.Db.KeyDeleteAsync([h.Keys.QueueLine(sale.PhaseId), h.Keys.QueueSeq(sale.PhaseId)]);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(0);

        // No room at all (never created): nothing to do, nothing invented.
        (await admitter.TickAsync(Guid.NewGuid(), Ct)).AdmittedAfter.ShouldBe(0);
        await JoinAsync(h, sale, 3);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(3);
    }

    [Fact]
    public async Task Tick_is_noop_unless_open()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var pre = h.Sale(TimeSpan.FromMinutes(10));
        await JoinAsync(h, pre, 5);
        var held = h.Sale(TimeSpan.FromMinutes(-1));
        await JoinAsync(h, held, 5);
        await h.Db.HashSetAsync(h.Keys.QueueState(held.PhaseId), "status", "AllHeld");
        var admitter = AdmitterFor(h);

        (await admitter.TickAsync(pre.PhaseId, Ct)).AdmittedAfter.ShouldBe(0);
        (await admitter.TickAsync(held.PhaseId, Ct)).AdmittedAfter.ShouldBe(0);
    }

    [Fact]
    public async Task Step_is_limited_by_free_capacity()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 50; o.MaxActiveShoppers = 10; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = await JoinAsync(h, sale, 100);
        var admitter = AdmitterFor(h);

        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(10);
        foreach (var user in users.Take(10))
        {
            (await h.Room.StatusAsync(sale, user, Ct)).Active.ShouldBeTrue();
        }

        // Everyone admitted is shopping, so the admitter has no room to move on.
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(10);
        await h.Room.LeaveAsync(sale, users[0], Ct);
        (await admitter.TickAsync(sale.PhaseId, Ct)).AdmittedAfter.ShouldBe(11);
    }

    [Fact]
    public async Task Active_never_exceeds_max_under_load()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 50; o.MaxActiveShoppers = 25; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = await JoinAsync(h, sale, 400);
        var admitter = AdmitterFor(h);
        var peak = 0L;

        using var stop = new CancellationTokenSource();
        var shoppers = users.Select(user => Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    await h.Room.StatusAsync(sale, user, Ct);
                }
            },
            Ct)).ToArray();

        for (var i = 0; i < 30; i++)
        {
            await admitter.TickAsync(sale.PhaseId, Ct);
            peak = Math.Max(peak, await h.Db.SortedSetLengthAsync(h.Keys.QueueActive(sale.PhaseId)));
        }

        await stop.CancelAsync();
        await Task.WhenAll(shoppers);
        peak = Math.Max(peak, await h.Db.SortedSetLengthAsync(h.Keys.QueueActive(sale.PhaseId)));

        peak.ShouldBeGreaterThan(0);
        peak.ShouldBeLessThanOrEqualTo(25);
    }

    [Fact]
    public async Task Expired_user_is_not_reclaimed_and_rejoin_goes_to_tail()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.MaxActiveShoppers = 100; o.ShoppingWindowMinutes = 10; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = await JoinAsync(h, sale, 5);
        var admitter = AdmitterFor(h);
        await admitter.TickAsync(sale.PhaseId, Ct);
        var user = users[0];
        (await h.Room.StatusAsync(sale, user, Ct)).Active.ShouldBeTrue();

        h.Time.Advance(TimeSpan.FromMinutes(11));

        // Even though number <= admitted, the expired shopper stays out.
        var afterExpiry = await h.Room.StatusAsync(sale, user, Ct);
        afterExpiry.Active.ShouldBeFalse();
        afterExpiry.Done.ShouldBeTrue();
        (await h.Room.StatusAsync(sale, user, Ct)).Claimed.ShouldBeFalse();
        (await h.Room.IsActiveAsync(sale.PhaseId, user, Ct)).ShouldBeFalse();

        var rejoined = await h.Room.JoinAsync(sale, user, Ct);

        rejoined.Number.ShouldBe(6);
        (await h.Room.StatusAsync(sale, user, Ct)).Done.ShouldBeFalse();
    }

    [Fact]
    public async Task Tick_sweeps_expired_shoppers_and_frees_their_slots()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.MaxActiveShoppers = 3; o.AdmissionRatePerSecond = 50; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = await JoinAsync(h, sale, 10);
        var admitter = AdmitterFor(h);
        await admitter.TickAsync(sale.PhaseId, Ct);
        foreach (var user in users.Take(3))
        {
            await h.Room.StatusAsync(sale, user, Ct);
        }

        h.Time.Advance(TimeSpan.FromMinutes(11));
        var tick = await admitter.TickAsync(sale.PhaseId, Ct);

        tick.AdmittedAfter.ShouldBe(6);
        (await h.Db.SortedSetLengthAsync(h.Keys.QueueActive(sale.PhaseId))).ShouldBe(0);
        (await h.Db.HashLengthAsync(h.Keys.QueueTokens(sale.PhaseId))).ShouldBe(0);
    }

    [Fact]
    public async Task Tick_updates_rate_ema_only_while_there_is_a_backlog()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 50; o.MaxActiveShoppers = 1_000; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        await JoinAsync(h, sale, 60);
        var admitter = AdmitterFor(h);

        await admitter.TickAsync(sale.PhaseId, Ct);
        var afterBacklog = await h.Room.GetRateEmaAsync(sale.PhaseId, Ct);
        await admitter.TickAsync(sale.PhaseId, Ct); // admits the last 10
        await admitter.TickAsync(sale.PhaseId, Ct); // empty queue: no new sample
        await admitter.TickAsync(sale.PhaseId, Ct);
        var later = await h.Room.GetRateEmaAsync(sale.PhaseId, Ct);

        afterBacklog.ShouldBe(50);
        later.ShouldBeGreaterThan(40);
        later.ShouldBeLessThan(50);
        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "rateEma")).HasValue.ShouldBeTrue();
    }

    [Fact]
    public async Task Close_marks_the_room_closed_and_stops_new_claims()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = await JoinAsync(h, sale, 2);
        var admitter = AdmitterFor(h);
        await admitter.TickAsync(sale.PhaseId, Ct);

        await admitter.CloseAsync(sale.PhaseId, Ct);

        var status = await h.Room.StatusAsync(sale, users[0], Ct);
        status.Status.ShouldBe(RoomStatus.Closed);
        status.Active.ShouldBeFalse();
        (await AdmittedAsync(h, sale)).ShouldBe(2);
    }
}
