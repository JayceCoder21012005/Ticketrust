using System.Text.RegularExpressions;

namespace TicketRush.Domain.Events.Layout;

/// <summary>
/// Turns a <see cref="SeatLayoutDefinition"/> into seats. The web preview runs a TypeScript port of this
/// (<c>web/src/lib/organizer/seat-layout.ts</c>); <c>shared/fixtures/seat-layout.json</c> keeps both identical, so
/// change them together.
/// </summary>
public static partial class SeatLayoutGenerator
{
    public const double CanvasWidth = 1000;
    public const double CanvasHeight = 700;
    public const double SeatPitch = 24;
    public const double RowPitch = 28;
    public const double MaxCurve = 0.5;
    public const int MaxRows = 100;
    public const int MaxSeatsPerRow = 100;
    public const int MaxSeats = 20_000;
    public const int MaxSections = 50;

    public static IReadOnlyList<LayoutError> Validate(SeatLayoutDefinition definition, IReadOnlyDictionary<Guid, TicketKind> tiers)
    {
        var errors = new List<LayoutError>();
        if (definition.Sections.Count == 0)
        {
            errors.Add(new LayoutError(string.Empty, "sections", "Add at least one section."));
            return errors;
        }

        // Standing sections have no seats, so the seat cap alone doesn't bound the stored layout.
        if (definition.Sections.Count > MaxSections)
        {
            errors.Add(new LayoutError(string.Empty, "sections", $"A layout may have at most {MaxSections} sections."));
            return errors;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var section in definition.Sections)
        {
            var code = section.Code ?? string.Empty;
            void Fail(string field, string message) => errors.Add(new LayoutError(code, field, message));

            if (!SectionCode().IsMatch(code))
            {
                Fail("code", "Use 1–8 letters or digits.");
            }
            else if (!seen.Add(code))
            {
                Fail("code", "Section codes must be unique.");
            }

            var expectedKind = section.Kind == SectionKind.Standing ? TicketKind.GeneralAdmission : TicketKind.Seated;
            if (!tiers.TryGetValue(section.TierId, out var kind) || kind != expectedKind)
            {
                Fail("tierId", section.Kind == SectionKind.Standing
                    ? "A standing section needs a general admission tier of this event."
                    : "A seated section needs a seated tier of this event.");
            }

            if (section.Kind == SectionKind.Standing)
            {
                continue;
            }

            var shapeErrorsBefore = errors.Count;
            if (section.Rows is not (>= 1 and <= MaxRows))
            {
                Fail("rows", $"Rows must be between 1 and {MaxRows}.");
            }

            if (section.SeatsPerRow is not (>= 1 and <= MaxSeatsPerRow))
            {
                Fail("seatsPerRow", $"Seats per row must be between 1 and {MaxSeatsPerRow}.");
            }

            if (section.Curve is < 0 or > MaxCurve || double.IsNaN(section.Curve))
            {
                Fail("curve", $"Curve must be between 0 and {MaxCurve}.");
            }

            if (section.Origin is not { X: >= 0 and <= CanvasWidth, Y: >= 0 and <= CanvasHeight })
            {
                Fail("origin", $"Origin must be inside the {CanvasWidth}×{CanvasHeight} canvas.");
            }

            if (section is { Rows: > 0, SeatsPerRow: > 0 })
            {
                total += (long)section.Rows.Value * section.SeatsPerRow.Value;
            }

            if (errors.Count == shapeErrorsBefore)
            {
                // The origin is the top-left seat; the far corner must stay on the canvas too, or the preview hides seats.
                var width = (section.SeatsPerRow!.Value - 1) * SeatPitch;
                if (section.Origin!.X + width > CanvasWidth)
                {
                    Fail("seatsPerRow", $"The section reaches past the right edge of the {CanvasWidth}×{CanvasHeight} canvas.");
                }

                if (section.Origin.Y + ((section.Rows!.Value - 1) * RowPitch) + (section.Curve * width) > CanvasHeight)
                {
                    Fail("rows", $"The section reaches past the bottom edge of the {CanvasWidth}×{CanvasHeight} canvas.");
                }
            }
        }

        if (total > MaxSeats)
        {
            errors.Add(new LayoutError(string.Empty, "sections", $"A layout may have at most {MaxSeats} seats."));
        }

        return errors;
    }

    /// <summary>Seats in (section order, row, number) order with contiguous indexes from 0. Assumes a valid definition.</summary>
    public static IReadOnlyList<GeneratedSeat> Generate(SeatLayoutDefinition definition)
    {
        var seats = new List<GeneratedSeat>();
        foreach (var section in definition.Sections.Where(s => s.Kind == SectionKind.Seated))
        {
            var rows = section.Rows!.Value;
            var perRow = section.SeatsPerRow!.Value;
            var origin = section.Origin!;
            var width = (perRow - 1) * SeatPitch;

            for (var r = 0; r < rows; r++)
            {
                var row = RowLabel(r);
                for (var n = 0; n < perRow; n++)
                {
                    var t = perRow == 1 ? 0.5 : (double)n / (perRow - 1);
                    var sag = section.Curve * width * (1 - Math.Pow((2 * t) - 1, 2));
                    var x = origin.X + (n * SeatPitch);
                    var y = origin.Y + (r * RowPitch) + sag;
                    seats.Add(new GeneratedSeat(seats.Count, section.Code, row, (n + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), Round(x), Round(y), section.TierId));
                }
            }
        }

        return seats;
    }

    /// <summary>0 → A, 25 → Z, 26 → AA, 27 → AB (spreadsheet-style).</summary>
    public static string RowLabel(int zeroBased)
    {
        var label = string.Empty;
        for (var n = zeroBased + 1; n > 0; n = (n - 1) / 26)
        {
            label = (char)('A' + ((n - 1) % 26)) + label;
        }

        return label;
    }

    // Same arithmetic as JavaScript's Math.round (floor(v + 0.5)) so both ports agree on every coordinate; inputs are ≥ 0.
    private static decimal Round(double value) => (decimal)(Math.Floor((value * 100) + 0.5) / 100);

    [GeneratedRegex("^[A-Za-z0-9]{1,8}$")]
    private static partial Regex SectionCode();
}
