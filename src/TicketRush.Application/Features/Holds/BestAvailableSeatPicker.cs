using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events.Layout;

namespace TicketRush.Application.Features.Holds;

/// <summary>Chooses seats for "Chọn chỗ tốt nhất" (spec 07, Strategy).</summary>
public interface IBestAvailablePicker
{
    /// <returns>Up to <paramref name="max"/> non-overlapping candidates, best first; each is ascending seat indexes.</returns>
    IReadOnlyList<IReadOnlyList<int>> Candidates(HoldCatalogEntry catalog, ISet<int> unavailable, Guid tierId, int quantity, int max = 3);
}

/// <summary>
/// N adjacent free seats in one row of the tier (the layout generator numbers a row's seats with consecutive indexes),
/// scored by the distance of their centre to the stage centre, so nearer and more central runs win.
/// </summary>
public sealed class BestAvailableSeatPicker : IBestAvailablePicker
{
    /// <summary>Middle of the stage drawn at the top of the 1000×700 layout canvas (spec 05 preview).</summary>
    private const double StageX = SeatLayoutGenerator.CanvasWidth / 2;
    private const double StageY = 42;

    public IReadOnlyList<IReadOnlyList<int>> Candidates(HoldCatalogEntry catalog, ISet<int> unavailable, Guid tierId, int quantity, int max = 3)
    {
        if (quantity < 1)
        {
            return [];
        }

        var windows = catalog.Seats
            .Where(s => s.TierId == tierId)
            .GroupBy(s => (s.Section, s.Row))
            .SelectMany(row => Windows([.. row.OrderBy(s => s.Index)], unavailable, quantity))
            .OrderBy(Score)
            .ToList();

        var picked = new List<IReadOnlyList<int>>();
        var used = new HashSet<int>();
        foreach (var window in windows)
        {
            if (picked.Count == max)
            {
                break;
            }

            if (window.All(s => !used.Contains(s.Index)))
            {
                picked.Add([.. window.Select(s => s.Index)]);
                used.UnionWith(window.Select(s => s.Index));
            }
        }

        return picked;
    }

    private static IEnumerable<CatalogSeat[]> Windows(CatalogSeat[] row, ISet<int> unavailable, int quantity)
    {
        for (var start = 0; start + quantity <= row.Length; start++)
        {
            var window = row[start..(start + quantity)];
            var adjacent = window.Zip(window.Skip(1)).All(p => p.Second.Index == p.First.Index + 1);
            if (adjacent && window.All(s => !unavailable.Contains(s.Index)))
            {
                yield return window;
            }
        }
    }

    private static double Score(CatalogSeat[] window)
    {
        var x = (double)window.Average(s => s.X) - StageX;
        var y = (double)window.Average(s => s.Y) - StageY;
        return Math.Sqrt((x * x) + (y * y));
    }
}
