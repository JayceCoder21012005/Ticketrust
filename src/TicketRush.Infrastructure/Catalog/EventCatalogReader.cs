using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Catalog;

/// <summary>
/// Postgres implementation of the catalog. Every query is <c>AsNoTracking</c> and projects straight to DTOs in
/// one round-trip; aggregates are never loaded. Draft and cancelled events are invisible.
/// </summary>
internal sealed class EventCatalogReader(AppDbContext db, TimeProvider time) : IEventCatalogReader
{
    public async Task<EventPage> ListAsync(ListEventsQuery query, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var events = db.Events.AsNoTracking().Where(e => e.Status == EventStatus.Published && e.EndsAt > now);

        if (query.Q is { } q)
        {
            events = events.Where(e => CatalogDbFunctions.Matches(
                EF.Property<NpgsqlTsVector>(e, CatalogSearch.VectorProperty),
                EF.Property<string>(e, CatalogSearch.TextProperty),
                q));
        }

        if (query.City is { } city)
        {
            events = events.Where(e => e.City == city);
        }

        if (query.Category is { } category)
        {
            events = events.Where(e => e.Category == category);
        }

        if (query.From is { } from)
        {
            events = events.Where(e => e.StartsAt >= from);
        }

        if (query.To is { } to)
        {
            events = events.Where(e => e.StartsAt <= to);
        }

        if (query.PriceMax is { } priceMax)
        {
            events = events.Where(e => e.Tiers.Any(t => t.Price.Amount <= priceMax));
        }

        if (EventCursor.TryDecode(query.Cursor, out var cursor))
        {
            // Row comparison (starts_at, id) > (@s, @i): uses the (status, starts_at, id) index.
            events = events.Where(e => EF.Functions.GreaterThan(
                ValueTuple.Create(e.StartsAt, e.Id),
                ValueTuple.Create(cursor.StartsAt, cursor.Id)));
        }

        var rows = await (
                from e in events
                join v in db.Venues on e.VenueId equals v.Id
                orderby e.StartsAt, e.Id
                select new EventSummary(
                    e.Id,
                    e.Slug,
                    e.Title,
                    e.Category,
                    e.StartsAt,
                    v.Name,
                    e.City,
                    e.CoverImageUrl,
                    e.Tiers.Min(t => (long?)t.Price.Amount),
                    e.Phases.Where(p => p.OpensAt > now).Min(p => (DateTimeOffset?)p.OpensAt),
                    e.Phases.Any(p => p.OpensAt <= now && p.ClosesAt > now),
                    e.IsSoldOut))
            .Take(query.Limit + 1)
            .ToListAsync(ct);

        if (rows.Count <= query.Limit)
        {
            return new EventPage(rows, null);
        }

        rows.RemoveAt(rows.Count - 1);
        var last = rows[^1];
        return new EventPage(rows, EventCursor.Encode(last.StartsAt, last.Id));
    }

    public Task<EventDetail?> GetBySlugAsync(string slug, CancellationToken ct) =>
        (from e in Visible()
         join v in db.Venues on e.VenueId equals v.Id
         where e.Slug == slug
         select new EventDetail(
             e.Id,
             e.Slug,
             e.Title,
             e.Description,
             e.Category,
             e.Lineup,
             e.CoverImageUrl,
             e.StartsAt,
             e.EndsAt,
             e.Status,
             e.IsSoldOut,
             new VenueInfo(v.Id, v.Name, v.Address, v.City, v.MapUrl),
             e.Tiers.OrderBy(t => t.Price.Amount).ThenBy(t => t.Name)
                 .Select(t => new TierInfo(t.Id, t.Name, t.Price.Amount, t.Price.Currency, t.Kind, t.Color))
                 .ToList(),
             e.Phases.OrderBy(p => p.OpensAt)
                 .Select(p => new PhaseInfo(p.Id, p.Name, p.OpensAt, p.ClosesAt, p.QueueEnabled, p.MaxTicketsPerUser, p.AccessCodeHash != null))
                 .ToList()))
        .SingleOrDefaultAsync(ct)!;

    public async Task<SeatMap?> GetSeatMapAsync(Guid eventId, CancellationToken ct)
    {
        if (!await Visible().AnyAsync(e => e.Id == eventId, ct))
        {
            return null;
        }

        var seats = await db.EventSeats.AsNoTracking()
            .Where(s => s.EventId == eventId)
            .OrderBy(s => s.Index)
            .Select(s => new SeatInfo(s.Index, s.Section, s.Row, s.Number, s.X, s.Y, s.TierId))
            .ToListAsync(ct);

        var sections = seats
            .GroupBy(s => s.Section)
            .Select(group => new SeatMapSection(group.Key, group.Count()))
            .ToList();

        return new SeatMap(eventId, Version(seats), sections, seats);
    }

    public async Task<EventAvailability?> GetAvailabilityAsync(Guid eventId, CancellationToken ct)
    {
        var tiers = await Visible()
            .Where(e => e.Id == eventId)
            .Select(e => e.Tiers.Select(t => new
            {
                t.Id,
                t.Capacity,
                Seats = db.EventSeats.Count(s => s.TierId == t.Id),
            }).ToList())
            .SingleOrDefaultAsync(ct);

        // Until spec 07 tracks holds and sales in Redis, everything is available.
        return tiers is null
            ? null
            : new EventAvailability(0, [], tiers.Select(t => new TierRemaining(t.Id, t.Capacity ?? t.Seats)).ToList());
    }

    public async Task<IReadOnlyList<string>> ListCitiesAsync(CancellationToken ct)
    {
        var cities = await db.Venues.AsNoTracking().Select(v => v.City).Distinct().ToListAsync(ct);
        // Database collations differ between environments; sort for Vietnamese readers ("Đà Nẵng" before "Hà Nội").
        return cities.Order(StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), ignoreCase: false)).ToList();
    }

    public async Task<HomeSections> GetHomeAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var since = now.AddHours(-24);
        // The catalog is small (spec 04): every published, not ended event with its signals in one query, picked in memory.
        var candidates = await (
                from e in db.Events.AsNoTracking()
                where e.Status == EventStatus.Published && e.EndsAt > now
                join v in db.Venues on e.VenueId equals v.Id
                select new HomeCandidate(
                    new EventSummary(
                        e.Id,
                        e.Slug,
                        e.Title,
                        e.Category,
                        e.StartsAt,
                        v.Name,
                        e.City,
                        e.CoverImageUrl,
                        e.Tiers.Min(t => (long?)t.Price.Amount),
                        e.Phases.Where(p => p.OpensAt > now).Min(p => (DateTimeOffset?)p.OpensAt),
                        e.Phases.Any(p => p.OpensAt <= now && p.ClosesAt > now),
                        e.IsSoldOut),
                    db.Tickets.Count(t => t.EventId == e.Id && t.CreatedAt > since),
                    db.Tickets.Count(t => t.EventId == e.Id),
                    db.EventSeats.Count(s => s.EventId == e.Id) + (e.Tiers.Sum(t => t.Capacity) ?? 0),
                    db.EventFollows.Count(f => f.EventId == e.Id)))
            .ToListAsync(ct);

        return HomeSectionPicker.Pick(candidates, now);
    }

    private IQueryable<Event> Visible() =>
        db.Events.AsNoTracking().Where(e => e.Status == EventStatus.Published || e.Status == EventStatus.Ended);

    /// <summary>Content hash of the layout: unchanged seats give the same ETag on every instance.</summary>
    private static string Version(IEnumerable<SeatInfo> seats)
    {
        var canonical = new StringBuilder();
        foreach (var s in seats)
        {
            canonical.Append(CultureInfo.InvariantCulture, $"{s.Index}|{s.Section}|{s.Row}|{s.Number}|{s.X}|{s.Y}|{s.TierId:N}\n");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 12));
    }
}
