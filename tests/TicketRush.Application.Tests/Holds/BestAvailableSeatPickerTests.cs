using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Holds;

namespace TicketRush.Application.Tests.Holds;

public sealed class BestAvailableSeatPickerTests
{
    private static readonly HoldCatalogEntry Catalog = HoldTestCatalog.Build(DateTimeOffset.UtcNow);
    private readonly BestAvailableSeatPicker _picker = new();

    private IReadOnlyList<IReadOnlyList<int>> Pick(int quantity, params int[] unavailable) =>
        _picker.Candidates(Catalog, unavailable.ToHashSet(), HoldTestCatalog.Seated, quantity);

    [Fact]
    public void Picks_adjacent_seats_in_one_row_nearest_the_stage_centre()
    {
        var best = Pick(2)[0];

        best.ShouldBe([4, 5]); // row 1, seats 5-6: the front row, centred on x = 500
    }

    [Fact]
    public void Never_spans_a_taken_seat_or_a_row_end()
    {
        // Row 1 has only 1..3 free on the left and 7..10 on the right; 9 (row 1 seat 10) and 10 (row 2 seat 1) are not adjacent.
        var candidates = Pick(4, 3, 4, 5, 6);

        candidates.ShouldAllBe(c => c.Count == 4);
        candidates.ShouldAllBe(c => c.Zip(c.Skip(1)).All(p => p.Second == p.First + 1));
        candidates.ShouldAllBe(c => c.All(i => i / 10 == c[0] / 10));
        candidates.ShouldAllBe(c => !c.Intersect(new[] { 3, 4, 5, 6 }).Any());
    }

    [Fact]
    public void Candidates_do_not_overlap_each_other()
    {
        var candidates = Pick(3);

        candidates.SelectMany(c => c).Distinct().Count().ShouldBe(candidates.Sum(c => c.Count));
    }

    [Fact]
    public void Returns_nothing_when_no_run_is_long_enough()
    {
        int[] everyOther = [.. Enumerable.Range(0, 30).Where(i => i % 2 == 0)];

        Pick(2, everyOther).ShouldBeEmpty();
    }

    [Fact]
    public void Returns_at_most_three_candidates()
    {
        Pick(1).Count.ShouldBe(3);
    }

    [Fact]
    public void Only_seats_of_the_requested_tier_are_used()
    {
        _picker.Candidates(Catalog, new HashSet<int>(), HoldTestCatalog.Other, 1).ShouldBeEmpty();
    }
}
