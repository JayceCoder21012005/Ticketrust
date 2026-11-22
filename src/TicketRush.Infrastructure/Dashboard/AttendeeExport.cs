using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Features.Dashboard;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Dashboard;

/// <summary>
/// The attendee export (spec 15): every ticket of the event that is not voided, with its order's buyer, its current holder, tier,
/// seat and check-in. One untracked query read row by row (<c>AsAsyncEnumerable</c>), so memory stays flat however big the event.
/// The QR secret is never selected.
/// </summary>
public sealed class AttendeeExport(AppDbContext db) : IAttendeeExport
{
    public async IAsyncEnumerable<AttendeeRow> StreamAsync(Guid eventId, [EnumeratorCancellation] CancellationToken ct)
    {
        var rows =
            from t in db.Tickets.AsNoTracking()
            where t.EventId == eventId && t.Status != TicketStatus.Voided
            join o in db.Orders.AsNoTracking() on t.OrderId equals o.Id
            join tier in db.Set<TicketTier>().AsNoTracking() on t.TierId equals tier.Id
            join u in db.Users.AsNoTracking() on t.OwnerUserId equals u.Id
            from seat in db.EventSeats.AsNoTracking().Where(s => s.EventId == t.EventId && s.Index == t.SeatIndex).DefaultIfEmpty()
            orderby o.PaidAt, t.Id
            select new AttendeeRow(
                o.Id,
                o.Buyer.Name,
                o.Buyer.Email,
                u.FullName,
                tier.Name,
                seat == null ? null : seat.Section,
                seat == null ? null : seat.Row,
                seat == null ? null : seat.Number,
                o.PaidAt,
                t.Status == TicketStatus.CheckedIn,
                t.CheckedInAt);

        await foreach (var row in rows.AsAsyncEnumerable().WithCancellation(ct))
        {
            yield return row;
        }
    }
}
