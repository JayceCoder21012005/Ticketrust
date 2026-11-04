using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Organizer.Events;

/// <param name="Query">Matched without diacritics against title, lineup and city.</param>
public sealed record ListOrganizerEventsQuery(Guid OrganizerId, string? Query, EventStatus? Status);

public sealed class ListOrganizerEventsHandler(IAppDbContext db)
{
    public const int MaxRows = 200;

    public async Task<IReadOnlyList<OrganizerEventItem>> HandleAsync(ListOrganizerEventsQuery query, CancellationToken ct)
    {
        var events = db.Events.AsNoTracking().Where(e => e.OrganizerId == query.OrganizerId);

        if (query.Status is { } status)
        {
            events = events.Where(e => e.Status == status);
        }

        if (OrganizerText.Fold(query.Query ?? string.Empty).Trim() is { Length: > 0 } term)
        {
            events = events.Where(e => EF.Property<string>(e, EventShadowProperties.SearchText).Contains(term));
        }

        return await events
            .OrderByDescending(e => e.StartsAt)
            .Take(MaxRows)
            .Join(db.Venues, e => e.VenueId, v => v.Id, (e, v) => new OrganizerEventItem(
                e.Id,
                e.Slug,
                e.Title,
                e.Status,
                e.StartsAt,
                v.Name,
                db.EventSeats.Count(s => s.EventId == e.Id),
                e.CoverImageUrl))
            .ToListAsync(ct);
    }
}

public sealed record CreateEventDraftCommand(
    Guid OrganizerId,
    string Title,
    string Category,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed class CreateEventDraftValidator : AbstractValidator<CreateEventDraftCommand>
{
    public CreateEventDraftValidator()
    {
        RuleFor(c => c.Title).Must(t => t.Trim().Length is >= Event.MinTitleLength and <= Event.MaxTitleLength)
            .WithMessage($"Title must be {Event.MinTitleLength}–{Event.MaxTitleLength} characters.");
        RuleFor(c => c.Category).Must(EventCategories.IsKnown).WithMessage("Unknown category.");
        RuleFor(c => c.EndsAt).GreaterThan(c => c.StartsAt).WithMessage("The event must end after it starts.");
    }
}

public sealed class CreateEventDraftHandler(IAppDbContext db)
{
    public async Task<Result<OrganizerEvent>> HandleAsync(CreateEventDraftCommand command, CancellationToken ct)
    {
        if (await OrganizerEvents.LoadUsableVenueAsync(db, command.VenueId, command.OrganizerId, ct) is not { } venue)
        {
            return EventErrors.VenueNotFound;
        }

        var slug = await UniqueSlugAsync(OrganizerText.Slug(command.Title), ct);
        var evt = Event.CreateDraft(command.OrganizerId, slug, command.Title, command.Category, venue, command.StartsAt, command.EndsAt);
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
        return await OrganizerEvents.ToModelAsync(db, evt, ct);
    }

    /// <summary>"title", then "title-2", "title-3"… Slugs are global: the public url is <c>/events/{slug}</c>.</summary>
    private async Task<string> UniqueSlugAsync(string slug, CancellationToken ct)
    {
        var prefix = slug + "-";
        var taken = (await db.Events
                .Where(e => e.Slug == slug || e.Slug.StartsWith(prefix))
                .Select(e => e.Slug)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        if (!taken.Contains(slug))
        {
            return slug;
        }

        for (var n = 2; ; n++)
        {
            var candidate = $"{slug}-{n}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}

public sealed class GetOrganizerEventHandler(IAppDbContext db)
{
    public async Task<Result<OrganizerEvent>> HandleAsync(Guid eventId, Guid organizerId, CancellationToken ct) =>
        await OrganizerEvents.LoadOwnedAsync(db, eventId, organizerId, ct) is { } evt
            ? await OrganizerEvents.ToModelAsync(db, evt, ct)
            : EventErrors.NotFound;
}
