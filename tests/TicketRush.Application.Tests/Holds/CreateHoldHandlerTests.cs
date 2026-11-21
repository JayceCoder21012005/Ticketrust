using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Holds;
using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Holds;

public sealed class CreateHoldHandlerTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));
    private readonly IInventoryStore _store = Substitute.For<IInventoryStore>();
    private readonly IHoldCatalog _catalog = Substitute.For<IHoldCatalog>();
    private readonly IInventorySeeder _seeder = Substitute.For<IInventorySeeder>();
    private readonly Abstractions.Dashboard.IDashboardStore _dashboard = Substitute.For<Abstractions.Dashboard.IDashboardStore>();
    private readonly List<HoldRequest> _requests = [];

    public CreateHoldHandlerTests()
    {
        _catalog.GetAsync(HoldTestCatalog.EventId, Arg.Any<CancellationToken>()).Returns(HoldTestCatalog.Build(_time.GetUtcNow()));
        _store.IsWarmAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _store.GetSnapshotAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new InventorySnapshot(1, [], [], new Dictionary<Guid, int>(), true));
        _store.When(s => s.CreateHoldAsync(Arg.Any<HoldRequest>(), Arg.Any<CancellationToken>())).Do(call => _requests.Add(call.Arg<HoldRequest>()));
        StoreAnswers(Ok());
    }

    private HoldOutcome Ok() => new(HoldStatus.Ok, Guid.NewGuid(), _time.GetUtcNow().AddMinutes(8), [], null);

    private void StoreAnswers(params HoldOutcome[] outcomes)
    {
        var queue = new Queue<HoldOutcome>(outcomes);
        _store.CreateHoldAsync(Arg.Any<HoldRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    private CreateHoldHandler Handler(int maxSeatsPerHold = 8) =>
        new(_catalog, _store, _seeder, new BestAvailableSeatPicker(), _dashboard, Microsoft.Extensions.Options.Options.Create(new HoldsOptions { MaxSeatsPerHold = maxSeatsPerHold }), _time);

    private static CreateHoldCommand Command(int[]? seats = null, GaLine[]? ga = null, BestAvailableRequest? best = null, bool replace = false, Guid? phase = null) =>
        new(User, HoldTestCatalog.EventId, phase ?? HoldTestCatalog.Phase, seats, ga, best, replace);

    [Fact]
    public async Task Holding_seats_returns_prices_from_the_frozen_tiers_and_their_total()
    {
        var result = await Handler().HandleAsync(Command([4, 5], [new GaLine(HoldTestCatalog.Ga, 1)]), Ct);

        var hold = result.Value;
        hold.Seats.Select(s => (s.Index, s.Section, s.Row, s.Number, s.Price)).ShouldBe([(4, "A", "1", "5", 1_500_000L), (5, "A", "1", "6", 1_500_000L)]);
        hold.Ga.ShouldBe([new HeldGa(HoldTestCatalog.Ga, "Đứng", 1, 500_000)]);
        hold.Total.ShouldBe(3_500_000);
        hold.ServerNow.ShouldBe(_time.GetUtcNow());
        _requests.Single().MaxTickets.ShouldBe(4);
        _requests.Single().EventGaTiers.ShouldBe([HoldTestCatalog.Ga]);
    }

    [Fact]
    public async Task An_empty_selection_is_rejected()
    {
        (await Handler().HandleAsync(Command(), Ct)).Error!.Code.ShouldBe("hold.empty_selection");
        (await Handler().HandleAsync(Command(ga: [new GaLine(HoldTestCatalog.Ga, 0)]), Ct)).Error!.Code.ShouldBe("hold.empty_selection");
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    public async Task An_unknown_seat_is_invalid(int index)
    {
        (await Handler().HandleAsync(Command([index]), Ct)).Error!.Code.ShouldBe("hold.invalid_seat");
    }

    [Fact]
    public async Task A_ga_line_for_a_seated_tier_or_a_negative_quantity_is_invalid()
    {
        (await Handler().HandleAsync(Command(ga: [new GaLine(HoldTestCatalog.Seated, 1)]), Ct)).Error!.Code.ShouldBe("hold.invalid_seat");
        (await Handler().HandleAsync(Command(ga: [new GaLine(HoldTestCatalog.Ga, -2)]), Ct)).Error!.Code.ShouldBe("hold.invalid_seat");
    }

    [Fact]
    public async Task More_than_MaxSeatsPerHold_is_limit_exceeded()
    {
        (await Handler(maxSeatsPerHold: 2).HandleAsync(Command([1, 2, 3]), Ct)).Error!.Code.ShouldBe("hold.limit_exceeded");
        _requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Seat_conflicts_map_to_seats_unavailable_with_the_indexes()
    {
        StoreAnswers(HoldOutcome.Failed(HoldStatus.SeatUnavailable, [5]));

        var error = (await Handler().HandleAsync(Command([4, 5]), Ct)).Error!;

        error.Code.ShouldBe("hold.seats_unavailable");
        error.Extensions.ShouldNotBeNull()["unavailable"].ShouldBe(new[] { 5 });
    }

    [Theory]
    [InlineData(HoldStatus.GaInsufficient, "hold.insufficient_quantity")]
    [InlineData(HoldStatus.LimitExceeded, "hold.limit_exceeded")]
    [InlineData(HoldStatus.HasHold, "hold.already_exists")]
    public async Task Store_refusals_map_to_their_codes(HoldStatus status, string code)
    {
        StoreAnswers(HoldOutcome.Failed(status, tier: HoldTestCatalog.Ga));

        (await Handler().HandleAsync(Command([1]), Ct)).Error!.Code.ShouldBe(code);
    }

    [Fact]
    public async Task Best_available_tries_the_next_candidate_on_conflict()
    {
        StoreAnswers(HoldOutcome.Failed(HoldStatus.SeatUnavailable, [4]), Ok());

        var result = await Handler().HandleAsync(Command(best: new BestAvailableRequest(HoldTestCatalog.Seated, 2)), Ct);

        result.IsSuccess.ShouldBeTrue();
        _requests.Count.ShouldBe(2);
        _requests[0].SeatIndexes.ShouldBe([4, 5]);
        _requests[1].SeatIndexes.Intersect(_requests[0].SeatIndexes).ShouldBeEmpty();
    }

    [Fact]
    public async Task Best_available_gives_up_after_three_attempts()
    {
        StoreAnswers(HoldOutcome.Failed(HoldStatus.SeatUnavailable, [1]));

        var result = await Handler().HandleAsync(Command(best: new BestAvailableRequest(HoldTestCatalog.Seated, 1)), Ct);

        result.Error!.Code.ShouldBe("hold.insufficient_quantity");
        _requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Best_available_skips_seats_already_taken()
    {
        _store.GetSnapshotAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new InventorySnapshot(3, [4], [5], new Dictionary<Guid, int>(), true));

        await Handler().HandleAsync(Command(best: new BestAvailableRequest(HoldTestCatalog.Seated, 2)), Ct);

        _requests[0].SeatIndexes.Intersect(new[] { 4, 5 }).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_phase_that_is_not_in_the_event_is_an_invalid_admission()
    {
        (await Handler().HandleAsync(Command([1], phase: Guid.NewGuid()), Ct)).Error!.Code.ShouldBe("admission.invalid");
    }

    [Fact]
    public async Task A_closed_phase_is_rejected()
    {
        _time.Advance(TimeSpan.FromHours(3));

        (await Handler().HandleAsync(Command([1]), Ct)).Error!.Code.ShouldBe("queue.closed");
    }

    [Fact]
    public async Task A_cold_event_is_warmed_before_holding()
    {
        _store.IsWarmAsync(HoldTestCatalog.EventId, Arg.Any<CancellationToken>()).Returns(false);

        await Handler().HandleAsync(Command([1]), Ct);

        await _seeder.Received(1).SeedAsync(HoldTestCatalog.EventId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Redis_down_is_system_busy()
    {
        _store.CreateHoldAsync(Arg.Any<HoldRequest>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InventoryUnavailableException("down"));

        (await Handler().HandleAsync(Command([1]), Ct)).Error!.Code.ShouldBe("system.busy");
    }

    [Fact]
    public async Task Unknown_events_are_not_found()
    {
        var result = await Handler().HandleAsync(new CreateHoldCommand(User, Guid.NewGuid(), HoldTestCatalog.Phase, [1], null, null, false), Ct);

        result.Error!.Code.ShouldBe("event.not_found");
    }

    [Fact]
    public async Task A_hold_counts_once_as_held_on_the_dashboard_and_a_refused_one_does_not()
    {
        StoreAnswers(Ok());
        (await Handler().HandleAsync(Command([4]), Ct)).IsSuccess.ShouldBeTrue();
        await _dashboard.Received(1).RecordHeldAsync(HoldTestCatalog.EventId, Arg.Any<CancellationToken>());

        _dashboard.ClearReceivedCalls();
        StoreAnswers(HoldOutcome.Failed(HoldStatus.SeatUnavailable, [5]));
        (await Handler().HandleAsync(Command([5]), Ct)).IsSuccess.ShouldBeFalse();
        await _dashboard.DidNotReceive().RecordHeldAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
