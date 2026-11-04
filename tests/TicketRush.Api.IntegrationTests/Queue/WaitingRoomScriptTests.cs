using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class WaitingRoomScriptTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Assigns numbers to these pre-queue users in the given order, like the opener does after shuffling.</summary>
    private static async Task AssignAsync(QueueRoomHarness h, SaleWindow sale, params Guid[] users) =>
        await QueueScripts.AssignBatchAsync(h.Db, h.Keys, sale.PhaseId, users.Select(u => u.ToString()).ToArray());

    [Fact]
    public async Task Join_before_open_goes_to_pre_without_number()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        var user = Guid.NewGuid();

        var result = await h.Room.JoinAsync(sale, user, Ct);

        result.Status.ShouldBe(RoomStatus.Pre);
        result.Number.ShouldBeNull();
        (await h.Db.SetContainsAsync(h.Keys.QueuePre(sale.PhaseId), user.ToString())).ShouldBeTrue();
    }

    [Fact]
    public async Task Join_is_idempotent_and_returns_same_number()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var user = Guid.NewGuid();

        var first = await h.Room.JoinAsync(sale, user, Ct);
        var second = await h.Room.JoinAsync(sale, user, Ct);
        var other = await h.Room.JoinAsync(sale, Guid.NewGuid(), Ct);

        first.Status.ShouldBe(RoomStatus.Open);
        first.Number.ShouldBe(1);
        second.Number.ShouldBe(1);
        other.Number.ShouldBe(2);
    }

    [Fact]
    public async Task Join_during_opening_gets_number_after_all_pre_users()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(10));
        Guid[] pre = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (var user in pre)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        (await QueueScripts.BeginOpenAsync(h.Db, h.Keys, sale.PhaseId)).ShouldBeTrue();
        var late = Guid.NewGuid();
        var lateJoin = await h.Room.JoinAsync(sale, late, Ct);
        lateJoin.Status.ShouldBe(RoomStatus.Opening);
        lateJoin.Number.ShouldBeNull();

        await AssignAsync(h, sale, pre);
        await QueueScripts.FinalizeOpenAsync(h.Db, h.Keys, sale.PhaseId);

        var numbers = new List<long>();
        foreach (var user in pre.Append(late))
        {
            numbers.Add((await h.Room.JoinAsync(sale, user, Ct)).Number!.Value);
        }

        numbers.Take(3).Order().ShouldBe([1L, 2L, 3L]);
        numbers[3].ShouldBe(4);
        (await h.Db.StringGetAsync(h.Keys.QueueSeq(sale.PhaseId))).ToString().ShouldBe("4");
        (await h.Db.HashGetAsync(h.Keys.QueueState(sale.PhaseId), "status")).ToString().ShouldBe("Open");
    }

    [Fact]
    public async Task Claim_respects_MaxActiveShoppers()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => o.MaxActiveShoppers = 2);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var users = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        await h.Db.StringSetAsync(h.Keys.QueueAdmitted(sale.PhaseId), 5);

        var results = new List<ClaimResult>();
        foreach (var user in users)
        {
            results.Add(await h.Room.StatusAsync(sale, user, Ct));
        }

        results.Count(r => r.Claimed).ShouldBe(2);
        results.Count(r => r.Active).ShouldBe(2);
        (await h.Db.SortedSetLengthAsync(h.Keys.QueueActive(sale.PhaseId))).ShouldBe(2);
    }

    [Fact]
    public async Task Claim_is_not_granted_before_the_admitter_reaches_the_users_number()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var user = Guid.NewGuid();
        await h.Room.JoinAsync(sale, user, Ct);

        var result = await h.Room.StatusAsync(sale, user, Ct);

        result.Active.ShouldBeFalse();
        result.Number.ShouldBe(1);
        result.Admitted.ShouldBe(0);
    }

    [Fact]
    public async Task Concurrent_claims_return_same_token()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1));
        var user = Guid.NewGuid();
        await h.Room.JoinAsync(sale, user, Ct);
        await h.Db.StringSetAsync(h.Keys.QueueAdmitted(sale.PhaseId), 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => h.Room.StatusAsync(sale, user, Ct)));

        results.Select(r => r.Token).Distinct().Count().ShouldBe(1);
        results[0].Token.ShouldNotBeNullOrEmpty();
        results.Count(r => r.Claimed).ShouldBe(1);
        (await h.Db.SortedSetLengthAsync(h.Keys.QueueActive(sale.PhaseId))).ShouldBe(1);
    }

    [Fact]
    public async Task Claim_instant_for_non_queue_phase_admits_without_admitter()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var sale = h.Sale(TimeSpan.FromMinutes(-1), queueEnabled: false);
        var user = Guid.NewGuid();
        await h.Room.JoinAsync(sale, user, Ct);

        var result = await h.Room.StatusAsync(sale, user, Ct);

        result.Active.ShouldBeTrue();
        result.Token.ShouldNotBeNullOrEmpty();
        result.ActiveUntil.ShouldNotBeNull().ShouldBe(h.Time.GetUtcNow().AddMinutes(h.Options.ShoppingWindowMinutes), TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public async Task Leave_frees_the_slot_and_marks_the_user_done()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => o.MaxActiveShoppers = 1);
        var sale = h.Sale(TimeSpan.FromMinutes(-1), queueEnabled: false);
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await h.Room.JoinAsync(sale, first, Ct);
        await h.Room.JoinAsync(sale, second, Ct);
        (await h.Room.StatusAsync(sale, first, Ct)).Active.ShouldBeTrue();
        (await h.Room.StatusAsync(sale, second, Ct)).Active.ShouldBeFalse();

        await h.Room.LeaveAsync(sale, first, Ct);

        (await h.Room.IsActiveAsync(sale.PhaseId, first, Ct)).ShouldBeFalse();
        (await h.Room.StatusAsync(sale, first, Ct)).Done.ShouldBeTrue();
        (await h.Room.StatusAsync(sale, second, Ct)).Active.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_unreachable_throws_QueueUnavailableException()
    {
        var options = ConfigurationOptions.Parse("127.0.0.1:1,connectTimeout=200,abortConnect=false,syncTimeout=300,asyncTimeout=300");
        await using var dead = await ConnectionMultiplexer.ConnectAsync(options);
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var room = new RedisWaitingRoom(
            dead, h.Keys, Microsoft.Extensions.Options.Options.Create(h.Options), new FakeTokenFactory(), h.Time);

        await Should.ThrowAsync<QueueUnavailableException>(() => room.JoinAsync(h.Sale(TimeSpan.Zero), Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task IsMember_is_true_for_pre_queue_and_line_members_and_false_after_leaving()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers);
        var pre = h.Sale(TimeSpan.FromMinutes(10));
        var open = h.Sale(TimeSpan.FromMinutes(-1));
        var (a, b, stranger) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await h.Room.JoinAsync(pre, a, Ct);
        await h.Room.JoinAsync(open, b, Ct);

        (await h.Room.IsMemberAsync(pre, a, Ct)).ShouldBeTrue();
        (await h.Room.IsMemberAsync(open, b, Ct)).ShouldBeTrue();
        (await h.Room.IsMemberAsync(open, stranger, Ct)).ShouldBeFalse();

        await h.Room.LeaveAsync(open, b, Ct);
        (await h.Room.IsMemberAsync(open, b, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Redis_timeouts_and_errors_are_reported_as_QueueUnavailable_not_as_a_500()
    {
        // RedisTimeoutException derives from TimeoutException, not RedisException: both must become 503 system.busy.
        Exception[] failures =
        [
            new RedisTimeoutException("Timeout awaiting response", CommandStatus.Unknown),
            new RedisConnectionException(ConnectionFailureType.SocketFailure, "socket closed"),
            new RedisServerException("BUSY Redis is busy running a script"),
            new ObjectDisposedException("multiplexer"),
        ];

        foreach (var failure in failures)
        {
            var thrown = await Should.ThrowAsync<QueueUnavailableException>(() => RedisWaitingRoom.GuardAsync<int>(() => throw failure));
            thrown.InnerException.ShouldBe(failure);
        }

        // Anything else is a bug, not an outage, and must not be disguised.
        await Should.ThrowAsync<InvalidOperationException>(() => RedisWaitingRoom.GuardAsync<int>(() => throw new InvalidOperationException()));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Every_queue_key_expires_a_day_after_the_sale_closes()
    {
        await using var h = await QueueRoomHarness.CreateAsync(containers, o => { o.AdmissionRatePerSecond = 50; o.MaxActiveShoppers = 100; });
        var sale = h.Sale(TimeSpan.FromMinutes(-1), TimeSpan.FromHours(2));
        var users = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            await h.Room.JoinAsync(sale, user, Ct);
        }

        await new WaitingRoomAdmitter(h.Redis, h.Keys, Microsoft.Extensions.Options.Options.Create(h.Options), h.Time).TickAsync(sale.PhaseId, Ct);
        await h.Room.StatusAsync(sale, users[0], Ct); // claims: active + tokens
        await h.Room.LeaveAsync(sale, users[1], Ct); // done

        var expected = sale.ClosesAt.AddDays(1) - DateTimeOffset.UtcNow;
        RedisKey[] all =
        [
            h.Keys.QueueState(sale.PhaseId), h.Keys.QueueLine(sale.PhaseId), h.Keys.QueueSeq(sale.PhaseId), h.Keys.QueueAdmitted(sale.PhaseId),
            h.Keys.QueueActive(sale.PhaseId), h.Keys.QueueTokens(sale.PhaseId), h.Keys.QueueDone(sale.PhaseId),
        ];
        foreach (var key in all)
        {
            var ttl = await h.Db.KeyTimeToLiveAsync(key);
            ttl.ShouldNotBeNull($"{key} has no expiry");
            ttl.Value.ShouldBeInRange(expected - TimeSpan.FromMinutes(1), expected + TimeSpan.FromSeconds(5));
        }
    }
}
