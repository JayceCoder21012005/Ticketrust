using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Application.Features.Organizer;

/// <summary>Shared steps of the organizer use cases: ownership-filtered loading, versions and the response model.</summary>
public static class OrganizerEvents
{
    /// <summary>Another organizer's event is indistinguishable from a missing one (404, never 403).</summary>
    public static Task<Event?> LoadOwnedAsync(IAppDbContext db, Guid eventId, Guid organizerId, CancellationToken ct) =>
        db.Events
            .Include(e => e.Tiers)
            .Include(e => e.Phases)
            .SingleOrDefaultAsync(e => e.Id == eventId && e.OrganizerId == organizerId, ct);

    /// <summary>Shared venues (no owner) and the organizer's own.</summary>
    public static Task<Venue?> LoadUsableVenueAsync(IAppDbContext db, Guid venueId, Guid organizerId, CancellationToken ct) =>
        db.Venues.SingleOrDefaultAsync(v => v.Id == venueId && (v.OrganizerId == null || v.OrganizerId == organizerId), ct);

    public static bool IsVersion(string? version) =>
        uint.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    /// <summary>
    /// False when the client edited an older version. Otherwise the client's version becomes the original value, so a
    /// write racing this one still fails in the database (<c>WHERE xmin = …</c>).
    /// </summary>
    public static bool MatchesVersion(IAppDbContext db, Event evt, string version)
    {
        var property = db.Entry(evt).Property<uint>(EventShadowProperties.Version);
        if (!uint.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var expected)
            || property.CurrentValue != expected)
        {
            return false;
        }

        property.OriginalValue = expected;
        return true;
    }

    public static async Task<OrganizerEvent> ToModelAsync(IAppDbContext db, Event evt, CancellationToken ct)
    {
        var seatsPerTier = await SeatsPerTierAsync(db, evt.Id, ct);
        var version = db.Entry(evt).Property<uint>(EventShadowProperties.Version).CurrentValue;

        return new OrganizerEvent(
            evt.Id,
            evt.Slug,
            evt.Title,
            evt.Status,
            evt.Category,
            evt.VenueId,
            evt.StartsAt,
            evt.EndsAt,
            evt.Description,
            evt.Lineup,
            evt.CoverImageUrl,
            evt.Tiers.Select(t => new TierItem(t.Id, t.Name, t.Price.Amount, t.Kind, t.Capacity, t.Color)).ToList(),
            evt.Layout,
            seatsPerTier,
            evt.Phases.OrderBy(p => p.OpensAt)
                .Select(p => new PhaseItem(p.Id, p.Name, p.OpensAt, p.ClosesAt, p.QueueEnabled, p.MaxTicketsPerUser, p.AccessCodeHash != null))
                .ToList(),
            version.ToString(CultureInfo.InvariantCulture));
    }

    public static async Task<IReadOnlyDictionary<Guid, int>> SeatsPerTierAsync(IAppDbContext db, Guid eventId, CancellationToken ct) =>
        await db.EventSeats
            .Where(s => s.EventId == eventId)
            .GroupBy(s => s.TierId)
            .Select(g => new { TierId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TierId, x => x.Count, ct);

    /// <summary>Loads the owned event, checks the version, applies <paramref name="change"/>, saves, returns the fresh model.</summary>
    public static async Task<Result<OrganizerEvent>> ChangeAsync(
        IAppDbContext db,
        Guid eventId,
        Guid organizerId,
        string version,
        Func<Event, Task<Result>> change,
        CancellationToken ct)
    {
        if (await LoadOwnedAsync(db, eventId, organizerId, ct) is not { } evt)
        {
            return EventErrors.NotFound;
        }

        if (!MatchesVersion(db, evt, version))
        {
            return EventErrors.ConcurrentEdit;
        }

        var result = await change(evt);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        await db.SaveChangesAsync(ct);
        return await ToModelAsync(db, evt, ct);
    }
}
