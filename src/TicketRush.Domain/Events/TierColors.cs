namespace TicketRush.Domain.Events;

/// <summary>
/// The colours an organizer may give a tier (seat map and price list). Each keeps white text at ≥ 4.5:1.
/// <c>web/src/features/organizer/tier-colors.ts</c> mirrors this list; a web test compares the two.
/// </summary>
public static class TierColors
{
    public static IReadOnlyList<string> All { get; } =
    [
        "#7c3aed",
        "#be123c",
        "#0f766e",
        "#1d4ed8",
        "#b45309",
        "#15803d",
        "#a21caf",
        "#334155",
    ];

    public static bool IsAllowed(string color) => All.Contains(color, StringComparer.OrdinalIgnoreCase);
}
