using System.Text.Json.Serialization;

namespace TicketRush.Domain.Events.Layout;

/// <summary>An organizer's layout as data (spec 05); <see cref="SeatLayoutGenerator"/> turns it into seats.</summary>
public sealed record SeatLayoutDefinition(IReadOnlyList<SectionDefinition> Sections);

/// <summary>
/// A seated block of <see cref="Rows"/> × <see cref="SeatsPerRow"/> seats whose top-left seat is at <see cref="Origin"/>,
/// or a standing area (no seats, General Admission tier). <see cref="Curve"/> 0..0.5 bends each row down in the middle.
/// </summary>
public sealed record SectionDefinition(
    string Code,
    Guid TierId,
    SectionKind Kind = SectionKind.Seated,
    int? Rows = null,
    int? SeatsPerRow = null,
    LayoutPoint? Origin = null,
    double Curve = 0);

[JsonConverter(typeof(JsonStringEnumConverter<SectionKind>))]
public enum SectionKind
{
    [JsonStringEnumMemberName("seated")]
    Seated,

    [JsonStringEnumMemberName("standing")]
    Standing,
}

public sealed record LayoutPoint(double X, double Y);

public sealed record GeneratedSeat(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId);

/// <param name="Section">The section code, or empty for a layout-wide error.</param>
/// <param name="Field">camelCase field name of the definition, e.g. <c>seatsPerRow</c>.</param>
public sealed record LayoutError(string Section, string Field, string Message);
