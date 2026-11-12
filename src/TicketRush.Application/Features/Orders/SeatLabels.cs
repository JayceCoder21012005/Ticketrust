using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;

namespace TicketRush.Application.Features.Orders;

/// <summary>The display label of a seat: section, row and number as the venue prints them.</summary>
internal sealed record SeatLabel(string Section, string Row, string Number);

/// <summary>
/// Loads seat labels from <c>event_seats</c>, the one place they are read: orders and the ticket wallet (spec 10) both show them.
/// The event may be cancelled by now, so no published-only catalog is used.
/// </summary>
internal static class SeatLabels
{
    /// <returns>The labels of the seats <paramref name="indexes"/> in each of <paramref name="eventIds"/>, keyed by event and seat index.</returns>
    public static async Task<Dictionary<(Guid EventId, int Index), SeatLabel>> LoadAsync(
        IAppDbContext db,
        IReadOnlyCollection<Guid> eventIds,
        IReadOnlyCollection<int> indexes,
        CancellationToken ct)
    {
        if (indexes.Count == 0)
        {
            return [];
        }

        var events = eventIds.ToArray();
        var seats = indexes.ToArray();
        var rows = await db.EventSeats.AsNoTracking()
            .Where(s => events.Contains(s.EventId) && seats.Contains(s.Index))
            .Select(s => new { s.EventId, s.Index, s.Section, s.Row, s.Number })
            .ToListAsync(ct);
        return rows.ToDictionary(s => (s.EventId, s.Index), s => new SeatLabel(s.Section, s.Row, s.Number));
    }
}
