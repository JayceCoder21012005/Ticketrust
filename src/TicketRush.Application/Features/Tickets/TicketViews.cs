using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Tickets;

/// <summary>One owned ticket joined with its event and venue. Never carries the QR secret.</summary>
internal sealed record TicketRow
{
    public Guid Id { get; init; }

    public Guid OrderId { get; init; }

    public Guid TierId { get; init; }

    public int? SeatIndex { get; init; }

    public TicketStatus Status { get; init; }

    public Guid EventId { get; init; }

    public string Slug { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? CoverImageUrl { get; init; }

    public DateTimeOffset StartsAt { get; init; }

    public DateTimeOffset EndsAt { get; init; }

    public EventStatus EventStatus { get; init; }

    public string VenueName { get; init; } = string.Empty;

    public string City { get; init; } = string.Empty;

    public TicketEventSummary ToSummary() => new(EventId, Slug, Title, CoverImageUrl, StartsAt, EndsAt, VenueName, City, EventStatus);
}

/// <summary>The only way the ticket use cases read tickets: every query starts from the owner's rows, so no id is ever looked up unscoped.</summary>
internal static class TicketQueries
{
    public static IQueryable<TicketRow> Owned(IAppDbContext db, Guid ownerUserId) =>
        from t in db.Tickets.AsNoTracking()
        where t.OwnerUserId == ownerUserId
        join e in db.Events.AsNoTracking() on t.EventId equals e.Id
        join v in db.Venues.AsNoTracking() on e.VenueId equals v.Id
        select new TicketRow
        {
            Id = t.Id,
            OrderId = t.OrderId,
            TierId = t.TierId,
            SeatIndex = t.SeatIndex,
            Status = t.Status,
            EventId = e.Id,
            Slug = e.Slug,
            Title = e.Title,
            CoverImageUrl = e.CoverImageUrl,
            StartsAt = e.StartsAt,
            EndsAt = e.EndsAt,
            EventStatus = e.Status,
            VenueName = v.Name,
            City = e.City,
        };

    /// <summary>Event not ended, not cancelled and the ticket not voided; everything else the user owns is "past".</summary>
    public static Expression<Func<TicketRow, bool>> IsUpcoming(DateTimeOffset now) =>
        r => r.EndsAt > now && r.EventStatus != EventStatus.Cancelled && r.EventStatus != EventStatus.Ended && r.Status != TicketStatus.Voided;

    public static Expression<Func<TicketRow, bool>> IsPast(DateTimeOffset now) =>
        r => !(r.EndsAt > now && r.EventStatus != EventStatus.Cancelled && r.EventStatus != EventStatus.Ended && r.Status != TicketStatus.Voided);
}

/// <summary>The rule for handing out a QR key, kept in one place: the ticket is valid and its event is still on.</summary>
public static class TicketDisplay
{
    public static bool CanShowQr(TicketStatus ticket, EventStatus evt, DateTimeOffset eventEndsAt, DateTimeOffset now) =>
        ticket == TicketStatus.Valid && evt is EventStatus.Published or EventStatus.Suspended && eventEndsAt > now;
}

/// <summary>Turns <see cref="TicketRow"/>s into <see cref="TicketItem"/>s: tier names, seat labels (from <see cref="SeatLabels"/>) and the holder's name.</summary>
internal sealed class TicketViews(IAppDbContext db, IUserDirectory users)
{
    public async Task<IReadOnlyList<TicketItem>> ToItemsAsync(IReadOnlyList<TicketRow> rows, Guid ownerUserId, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var seatIndexes = rows.Where(r => r.SeatIndex is not null).Select(r => r.SeatIndex!.Value).Distinct().ToArray();
        var labels = await SeatLabels.LoadAsync(db, [.. rows.Select(r => r.EventId).Distinct()], seatIndexes, ct);
        var tiers = await LoadTierNamesAsync([.. rows.Select(r => r.TierId).Distinct()], ct);
        var holder = (await users.GetContactAsync(ownerUserId, ct))?.FullName ?? string.Empty;

        return [.. rows.Select(r => new TicketItem(r.Id, r.OrderId, tiers.GetValueOrDefault(r.TierId, string.Empty), SeatOf(r, labels), r.Status, holder))];
    }

    private static TicketSeat? SeatOf(TicketRow row, Dictionary<(Guid EventId, int Index), SeatLabel> labels) =>
        row.SeatIndex is { } index && labels.GetValueOrDefault((row.EventId, index)) is { } label
            ? new TicketSeat(label.Section, label.Row, label.Number)
            : null;

    private async Task<Dictionary<Guid, string>> LoadTierNamesAsync(Guid[] tierIds, CancellationToken ct)
    {
        var rows = await db.Events.AsNoTracking()
            .SelectMany(e => e.Tiers)
            .Where(t => tierIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);
        return rows.ToDictionary(t => t.Id, t => t.Name);
    }
}
