using TicketRush.Application.Abstractions.Queue;
using TicketRush.Application.Features.Queue;

namespace TicketRush.Application.Tests.Queue;

public sealed class QueueLogicTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);

    private static SaleWindow Window(TimeSpan opensIn, TimeSpan? lasts = null, bool queue = true) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Now + opensIn, Now + opensIn + (lasts ?? TimeSpan.FromHours(2)), queue);

    private static ClaimResult Claim(
        RoomStatus status = RoomStatus.Open,
        long? number = 500,
        long admitted = 100,
        long seq = 900,
        bool active = false,
        bool done = false,
        bool inPre = false,
        string? token = null,
        DateTimeOffset? until = null) =>
        new(status, number, admitted, seq, 10, false, active, done, inPre, until, token);

    [Theory]
    [InlineData(10_001, 15_000)]
    [InlineData(10_000, 8_000)]
    [InlineData(1_001, 8_000)]
    [InlineData(1_000, 4_000)]
    [InlineData(101, 4_000)]
    [InlineData(100, 2_000)]
    [InlineData(1, 2_000)]
    public void Waiting_poll_interval_follows_the_position_table(long position, int expectedMs)
    {
        PollSchedule.NextPollMs(QueueClientStatus.Waiting, position, Now, Now, () => 0.5).ShouldBe(expectedMs);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(1.0, 1.2)]
    public void Waiting_poll_interval_is_jittered_by_20_percent(double random, double factor)
    {
        PollSchedule.NextPollMs(QueueClientStatus.Waiting, 5_000, Now, Now, () => random).ShouldBe((int)(8_000 * factor));
    }

    [Fact]
    public void Pre_poll_wakes_the_client_at_opensAt_plus_up_to_three_seconds()
    {
        var opensAt = Now.AddSeconds(10);

        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, opensAt, () => 0).ShouldBe(10_000);
        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, opensAt, () => 0.5).ShouldBe(11_500);
    }

    [Fact]
    public void Pre_poll_never_sleeps_more_than_two_minutes_and_never_less_than_one_second()
    {
        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, Now.AddMinutes(30), () => 0).ShouldBe(120_000);
    }

    [Fact]
    public void Pre_clients_still_waiting_after_opening_poll_every_2_to_5_seconds_not_in_lockstep()
    {
        // The room is still shuffling (or failing over): 100k clients must not all come back after exactly one second.
        var opened = Now.AddSeconds(-5);

        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, opened, () => 0).ShouldBe(2_000);
        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, opened, () => 0.5).ShouldBe(3_500);
        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, opened, () => 1).ShouldBe(5_000);
    }

    [Fact]
    public void Pre_poll_is_never_below_one_second_just_before_opening()
    {
        PollSchedule.NextPollMs(QueueClientStatus.Pre, 0, Now, Now.AddMilliseconds(200), () => 0).ShouldBe(1_000);
    }

    [Fact]
    public void Selector_prefers_the_open_phase_then_the_next_one_then_the_last_closed()
    {
        var closed = Window(TimeSpan.FromDays(-3), TimeSpan.FromDays(1));
        var open = Window(TimeSpan.FromMinutes(-5));
        var later = Window(TimeSpan.FromDays(2));
        var soonest = Window(TimeSpan.FromDays(1));

        PhaseSelector.Select([closed, later, open, soonest], Now)!.PhaseId.ShouldBe(open.PhaseId);
        PhaseSelector.Select([closed, later, soonest], Now)!.PhaseId.ShouldBe(soonest.PhaseId);
        PhaseSelector.Select([closed], Now)!.PhaseId.ShouldBe(closed.PhaseId);
        PhaseSelector.Select([], Now).ShouldBeNull();
    }

    private static readonly TimeSpan PreQueue = TimeSpan.FromMinutes(30);

    [Fact]
    public void Join_target_is_the_open_phase_when_no_next_phase_is_near()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var far = Window(TimeSpan.FromHours(3));

        PhaseSelector.SelectJoinTarget([open, far], Now, PreQueue)!.PhaseId.ShouldBe(open.PhaseId);
        PhaseSelector.SelectJoinTarget([open], Now, PreQueue)!.PhaseId.ShouldBe(open.PhaseId);
    }

    [Fact]
    public void Join_target_is_the_next_phase_when_its_pre_queue_window_is_running()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var next = Window(TimeSpan.FromMinutes(20));

        PhaseSelector.SelectJoinTarget([open, next], Now, PreQueue)!.PhaseId.ShouldBe(next.PhaseId);
    }

    [Fact]
    public void Join_target_stays_open_when_next_phase_is_just_outside_the_window()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var next = Window(PreQueue + TimeSpan.FromSeconds(1));

        PhaseSelector.SelectJoinTarget([open, next], Now, PreQueue)!.PhaseId.ShouldBe(open.PhaseId);
    }

    [Fact]
    public void Join_target_at_exactly_the_window_boundary_is_the_next_phase()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var next = Window(PreQueue);

        PhaseSelector.SelectJoinTarget([open, next], Now, PreQueue)!.PhaseId.ShouldBe(next.PhaseId);
    }

    [Fact]
    public void Join_target_ignores_a_next_phase_without_a_queue()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var noQueue = Window(TimeSpan.FromMinutes(10), queue: false);

        PhaseSelector.SelectJoinTarget([open, noQueue], Now, PreQueue)!.PhaseId.ShouldBe(open.PhaseId);
    }

    [Fact]
    public void Join_target_picks_the_earliest_of_several_upcoming_phases()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var second = Window(TimeSpan.FromMinutes(25));
        var first = Window(TimeSpan.FromMinutes(15));

        PhaseSelector.SelectJoinTarget([second, open, first], Now, PreQueue)!.PhaseId.ShouldBe(first.PhaseId);
    }

    [Fact]
    public void Join_target_without_an_open_phase_matches_the_existing_selector()
    {
        var closed = Window(TimeSpan.FromDays(-3), TimeSpan.FromDays(1));
        var soonest = Window(TimeSpan.FromMinutes(10));
        var later = Window(TimeSpan.FromDays(2));

        PhaseSelector.SelectJoinTarget([closed, later, soonest], Now, PreQueue)!.PhaseId.ShouldBe(soonest.PhaseId);
        PhaseSelector.SelectJoinTarget([closed], Now, PreQueue)!.PhaseId.ShouldBe(closed.PhaseId);
        PhaseSelector.SelectJoinTarget([], Now, PreQueue).ShouldBeNull();
    }

    [Fact]
    public void Room_candidates_are_open_then_next_only_inside_the_window_else_the_single_phase_of_the_moment()
    {
        var open = Window(TimeSpan.FromMinutes(-5));
        var near = Window(TimeSpan.FromMinutes(20));
        var far = Window(TimeSpan.FromHours(3));

        PhaseSelector.RoomCandidates([open, near], Now, PreQueue).Select(w => w.PhaseId).ShouldBe([open.PhaseId, near.PhaseId]);
        PhaseSelector.RoomCandidates([open, far], Now, PreQueue).Select(w => w.PhaseId).ShouldBe([open.PhaseId]);
        PhaseSelector.RoomCandidates([], Now, PreQueue).ShouldBeEmpty();
    }

    [Fact]
    public void Waiting_response_has_position_ahead_and_eta_from_the_observed_rate()
    {
        var sale = Window(TimeSpan.FromMinutes(-5));

        var response = QueueStatusMapper.Map(sale, Claim(number: 1_100, admitted: 100), Now, rateEma: 20, fallbackRatePerSecond: 50, () => 0.5)!;

        response.Status.ShouldBe(QueueClientStatus.Waiting);
        response.Position.ShouldBe(1_000);
        response.AheadOfYou.ShouldBe(999);
        response.EstimatedWaitSeconds.ShouldBe(50);
        response.ServerNow.ShouldBe(Now);
        response.OpensAt.ShouldBe(sale.OpensAt);
        response.AdmissionToken.ShouldBeNull();
    }

    [Fact]
    public void Eta_falls_back_to_the_configured_rate_when_nothing_was_observed()
    {
        var response = QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(number: 600, admitted: 100), Now, 0, 50, () => 0.5)!;

        response.EstimatedWaitSeconds.ShouldBe(10);
    }

    [Fact]
    public void Due_user_waiting_for_capacity_is_position_one()
    {
        var response = QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(number: 50, admitted: 100), Now, 10, 50, () => 0.5)!;

        response.Status.ShouldBe(QueueClientStatus.Waiting);
        response.Position.ShouldBe(1);
        response.AheadOfYou.ShouldBe(0);
    }

    [Fact]
    public void Admitted_response_carries_the_token_and_its_expiry()
    {
        var until = Now.AddMinutes(10);

        var response = QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(active: true, token: "tok", until: until), Now, 0, 50, () => 0.5)!;

        response.Status.ShouldBe(QueueClientStatus.Admitted);
        response.AdmissionToken.ShouldBe("tok");
        response.TokenExpiresAt.ShouldBe(until);
        response.Position.ShouldBeNull();
    }

    [Fact]
    public void Pre_user_has_no_position_and_a_wake_up_time()
    {
        var sale = Window(TimeSpan.FromMinutes(5));

        var response = QueueStatusMapper.Map(sale, Claim(RoomStatus.Pre, number: null, inPre: true), Now, 0, 50, () => 0)!;

        response.Status.ShouldBe(QueueClientStatus.Pre);
        response.Position.ShouldBeNull();
        response.PollAfterMs.ShouldBe(120_000);
    }

    [Fact]
    public void Expired_or_left_user_is_told_so()
    {
        var response = QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(number: 10, done: true), Now, 0, 50, () => 0.5)!;

        response.Status.ShouldBe(QueueClientStatus.Expired);
    }

    [Fact]
    public void Past_closesAt_is_closed_even_when_the_worker_has_not_marked_the_room_yet()
    {
        var sale = Window(TimeSpan.FromHours(-3), TimeSpan.FromHours(1));

        QueueStatusMapper.Map(sale, Claim(RoomStatus.Open), Now, 0, 50, () => 0.5)!.Status.ShouldBe(QueueClientStatus.Closed);
    }

    [Theory]
    [InlineData(RoomStatus.AllHeld, QueueClientStatus.AllHeld)]
    [InlineData(RoomStatus.SoldOut, QueueClientStatus.SoldOut)]
    [InlineData(RoomStatus.Closed, QueueClientStatus.Closed)]
    public void Room_wide_states_map_to_client_states(RoomStatus room, QueueClientStatus expected)
    {
        QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(room), Now, 0, 50, () => 0.5)!.Status.ShouldBe(expected);
    }

    [Fact]
    public void User_who_never_joined_maps_to_null()
    {
        QueueStatusMapper.Map(Window(TimeSpan.FromMinutes(-5)), Claim(number: null), Now, 0, 50, () => 0.5).ShouldBeNull();
    }
}
