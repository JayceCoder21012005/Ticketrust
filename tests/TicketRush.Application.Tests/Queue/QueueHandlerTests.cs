using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Application.Features.Queue;
using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Queue;

public sealed class QueueHandlerTests
{
    private const string GoodCode = "VIP-2026";
    private const string GoodHash = "hash-of-vip-2026";

    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid EventId = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly IWaitingRoom _room = Substitute.For<IWaitingRoom>();
    private readonly ISaleWindowProvider _sales = Substitute.For<ISaleWindowProvider>();
    private readonly IAccessCodeHasher _hasher = Substitute.For<IAccessCodeHasher>();

    public QueueHandlerTests()
    {
        _hasher.Hash(Arg.Any<string>()).Returns(call => call.Arg<string>() == GoodCode ? GoodHash : "wrong");
        _room.JoinAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new JoinResult(RoomStatus.Open, 7));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Open, 7, 0, 7, 0, false, false, false, false, null, null));
    }

    private SaleWindow Phase(TimeSpan opensIn, bool queue = true, string? hash = null)
    {
        var window = new SaleWindow(EventId, Guid.NewGuid(), _time.GetUtcNow() + opensIn, _time.GetUtcNow() + opensIn + TimeSpan.FromHours(2), queue);
        _sales.GetAsync(EventId, Arg.Any<CancellationToken>()).Returns(new EventSales(EventId, [new SalePhaseInfo(window, hash)]));
        return window;
    }

    private JoinQueueHandler JoinHandler() => new(_sales, _room, _hasher, Microsoft.Extensions.Options.Options.Create(new QueueOptions()), _time);

    private GetQueueStatusHandler StatusHandler() => new(_sales, _room, Microsoft.Extensions.Options.Options.Create(new QueueOptions()), _time);

    [Fact]
    public async Task Unconfirmed_email_is_rejected()
    {
        Phase(TimeSpan.FromMinutes(-1));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, EmailVerified: false, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.email_not_confirmed");
        await _room.DidNotReceiveWithAnyArgs().JoinAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unknown_event_is_not_found()
    {
        _sales.GetAsync(EventId, Arg.Any<CancellationToken>()).Returns((EventSales?)null);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("event.not_found");
    }

    [Fact]
    public async Task Join_before_the_window_is_not_open()
    {
        Phase(TimeSpan.FromMinutes(31)); // queued sales open their room 30 min early

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_open");
    }

    [Fact]
    public async Task Queued_sale_accepts_joins_from_thirty_minutes_before_opening()
    {
        Phase(TimeSpan.FromMinutes(29));
        _room.JoinAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>()).Returns(new JoinResult(RoomStatus.Pre, null));
        _room.StatusAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Pre, null, 0, 0, 0, false, false, false, true, null, null));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(QueueClientStatus.Pre);
    }

    [Fact]
    public async Task Non_queued_sale_does_not_open_early()
    {
        Phase(TimeSpan.FromMinutes(5), queue: false);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_open");
    }

    [Fact]
    public async Task Join_after_the_sale_closed_says_closed()
    {
        Phase(TimeSpan.FromHours(-3));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.closed");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nope")]
    public async Task Wrong_or_missing_access_code_is_rejected(string? code)
    {
        Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, code), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.invalid_access_code");
        await _room.DidNotReceiveWithAnyArgs().JoinAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Right_access_code_joins()
    {
        Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, GoodCode), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(Arg.Any<SaleWindow>(), User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_reloading_the_page_rejoins_without_retyping_the_presale_code()
    {
        var phase = Phase(TimeSpan.FromMinutes(-1), hash: GoodHash);
        _room.IsMemberAsync(phase, User, Arg.Any<CancellationToken>()).Returns(true);

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Redis_failure_maps_to_system_busy()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.JoinAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new QueueUnavailableException("down"));

        var result = await JoinHandler().HandleAsync(new JoinQueueCommand(User, EventId, true, null), TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("system.busy");
    }

    [Fact]
    public async Task Status_before_joining_is_not_joined()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Open, null, 0, 0, 0, false, false, false, false, null, null));

        var result = await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe("queue.not_joined");
    }

    [Fact]
    public async Task Status_redis_failure_maps_to_system_busy()
    {
        Phase(TimeSpan.FromMinutes(-1));
        _room.StatusAsync(Arg.Any<SaleWindow>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new QueueUnavailableException("down"));

        (await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken)).Error!.Code.ShouldBe("system.busy");
    }

    [Fact]
    public async Task Leave_removes_the_user_from_the_selected_phase()
    {
        var phase = Phase(TimeSpan.FromMinutes(-1));
        var handler = LeaveHandler();

        var result = await handler.HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).LeaveAsync(phase, User, Arg.Any<CancellationToken>());
    }

    private LeaveQueueHandler LeaveHandler() => new(_sales, _room, Microsoft.Extensions.Options.Options.Create(new QueueOptions()), _time);

    /// <summary>A presale that is open now and a general sale whose pre-queue window (30 min) is running or not.</summary>
    private (SaleWindow Open, SaleWindow Next) Adjacent(TimeSpan nextOpensIn, string? openHash = null, string? nextHash = null)
    {
        var now = _time.GetUtcNow();
        var open = new SaleWindow(EventId, Guid.NewGuid(), now.AddHours(-1), now + nextOpensIn, true);
        var next = new SaleWindow(EventId, Guid.NewGuid(), now + nextOpensIn, now + nextOpensIn + TimeSpan.FromHours(2), true);
        _sales.GetAsync(EventId, Arg.Any<CancellationToken>())
            .Returns(new EventSales(EventId, [new SalePhaseInfo(open, openHash), new SalePhaseInfo(next, nextHash)]));
        return (open, next);
    }

    private static JoinQueueCommand Join(string? code = null) => new(User, EventId, true, code);

    [Fact]
    public async Task A_new_joiner_inside_the_next_phases_window_is_queued_for_the_next_phase()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));

        var result = await JoinHandler().HandleAsync(Join(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().JoinAsync(open, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_of_the_open_phase_keeps_it_when_the_next_window_starts()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsMemberAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);

        var result = await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received().StatusAsync(open, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().StatusAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_of_the_open_phase_who_rejoins_inside_the_window_is_not_moved_and_needs_no_code()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20), openHash: GoodHash, nextHash: GoodHash);
        _room.IsMemberAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);

        var result = await JoinHandler().HandleAsync(Join(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(open, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().JoinAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Leave_inside_the_window_frees_the_room_the_user_is_in()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsMemberAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);

        (await LeaveHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await _room.Received(1).LeaveAsync(open, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().LeaveAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_of_the_next_phase_stays_there_for_status_leave_and_rejoin()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsMemberAsync(open, User, Arg.Any<CancellationToken>()).Returns(false);
        _room.IsMemberAsync(next, User, Arg.Any<CancellationToken>()).Returns(true);
        var ct = TestContext.Current.CancellationToken;

        (await StatusHandler().HandleAsync(User, EventId, ct)).IsSuccess.ShouldBeTrue();
        (await JoinHandler().HandleAsync(Join(), ct)).IsSuccess.ShouldBeTrue();
        (await LeaveHandler().HandleAsync(User, EventId, ct)).IsSuccess.ShouldBeTrue();

        await _room.Received().StatusAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().StatusAsync(open, User, Arg.Any<CancellationToken>());
        await _room.Received(1).JoinAsync(next, User, Arg.Any<CancellationToken>());
        await _room.Received(1).LeaveAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().LeaveAsync(open, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_next_phases_access_code_is_the_one_checked_for_a_new_joiner()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20), openHash: "hash-of-the-presale-code", nextHash: GoodHash);
        var ct = TestContext.Current.CancellationToken;

        (await JoinHandler().HandleAsync(Join("presale-code"), ct)).Error!.Code.ShouldBe("queue.invalid_access_code");
        await _room.DidNotReceiveWithAnyArgs().JoinAsync(default!, default, ct);

        (await JoinHandler().HandleAsync(Join(GoodCode), ct)).IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().JoinAsync(open, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_next_phase_without_a_code_does_not_ask_for_the_open_phases_code()
    {
        var (_, next) = Adjacent(TimeSpan.FromMinutes(20), openHash: GoodHash);

        var result = await JoinHandler().HandleAsync(Join(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Single_phase_events_behave_as_before_and_never_ask_the_room_about_membership()
    {
        Phase(TimeSpan.FromMinutes(-1));
        var ct = TestContext.Current.CancellationToken;

        (await StatusHandler().HandleAsync(User, EventId, ct)).IsSuccess.ShouldBeTrue();
        (await JoinHandler().HandleAsync(Join(), ct)).IsSuccess.ShouldBeTrue();
        (await LeaveHandler().HandleAsync(User, EventId, ct)).IsSuccess.ShouldBeTrue();

        await _room.DidNotReceiveWithAnyArgs().IsMemberAsync(default!, default, ct);
    }

    [Fact]
    public async Task An_open_phase_with_a_far_next_phase_is_one_candidate_and_asks_no_membership_question()
    {
        var (open, _) = Adjacent(TimeSpan.FromMinutes(45));
        var ct = TestContext.Current.CancellationToken;

        (await StatusHandler().HandleAsync(User, EventId, ct)).IsSuccess.ShouldBeTrue();

        await _room.DidNotReceiveWithAnyArgs().IsMemberAsync(default!, default, ct);
        await _room.Received(1).StatusAsync(open, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inside_the_window_a_status_poll_costs_at_most_one_extra_membership_check_for_a_member_of_the_open_phase()
    {
        var (open, _) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsMemberAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);
        var ct = TestContext.Current.CancellationToken;

        await StatusHandler().HandleAsync(User, EventId, ct);

        await _room.ReceivedWithAnyArgs(1).IsMemberAsync(default!, default, ct);
    }

    [Fact]
    public async Task A_user_who_left_the_open_phase_still_gets_that_rooms_status_inside_the_window()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsDoneAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);
        _room.StatusAsync(open, User, Arg.Any<CancellationToken>())
            .Returns(new ClaimResult(RoomStatus.Open, 7, 0, 7, 0, false, false, true, false, null, null));

        var result = await StatusHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken);

        result.Value.Status.ShouldBe(QueueClientStatus.Expired);
        await _room.DidNotReceive().StatusAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_user_who_is_only_done_in_the_open_phase_joins_the_next_phase_as_a_new_joiner()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsDoneAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);

        var result = await JoinHandler().HandleAsync(Join(), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        await _room.Received(1).JoinAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().JoinAsync(open, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceiveWithAnyArgs().IsDoneAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Active_membership_of_the_next_phase_wins_over_done_in_the_open_phase()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsDoneAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);
        _room.IsMemberAsync(next, User, Arg.Any<CancellationToken>()).Returns(true);

        (await LeaveHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await _room.Received(1).LeaveAsync(next, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceiveWithAnyArgs().IsDoneAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Leave_for_a_user_who_is_only_done_in_the_open_phase_targets_that_room()
    {
        var (open, next) = Adjacent(TimeSpan.FromMinutes(20));
        _room.IsDoneAsync(open, User, Arg.Any<CancellationToken>()).Returns(true);

        (await LeaveHandler().HandleAsync(User, EventId, TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await _room.Received(1).LeaveAsync(open, User, Arg.Any<CancellationToken>());
        await _room.DidNotReceive().LeaveAsync(next, User, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Single_candidate_paths_never_ask_the_done_question_either()
    {
        Phase(TimeSpan.FromMinutes(-1));
        var ct = TestContext.Current.CancellationToken;

        await StatusHandler().HandleAsync(User, EventId, ct);
        await LeaveHandler().HandleAsync(User, EventId, ct);

        await _room.DidNotReceiveWithAnyArgs().IsDoneAsync(default!, default, ct);
    }
}
