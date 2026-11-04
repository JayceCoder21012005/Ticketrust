using FluentValidation;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Organizer.Events;

public static class EventCoverKeys
{
    public static string PrefixFor(Guid eventId) => $"events/{eventId:N}/";
}

/// <summary>Editable in Draft and Published (spec 05: text and cover only after publishing).</summary>
public sealed record UpdateEventInfoCommand(
    Guid OrganizerId,
    Guid EventId,
    string Version,
    string Title,
    string Description,
    IReadOnlyList<string> Lineup,
    string? CoverImageUrl);

public sealed class UpdateEventInfoValidator : AbstractValidator<UpdateEventInfoCommand>
{
    public const int MaxDescriptionLength = 5000;
    public const int MaxLineup = 20;
    public const int MaxArtistLength = 100;

    public UpdateEventInfoValidator()
    {
        RuleFor(c => c.Version).Must(OrganizerEvents.IsVersion).WithMessage("Send the version you edited.");
        RuleFor(c => c.Title).Must(t => t.Trim().Length is >= Event.MinTitleLength and <= Event.MaxTitleLength)
            .WithMessage($"Title must be {Event.MinTitleLength}–{Event.MaxTitleLength} characters.");
        RuleFor(c => c.Description).MaximumLength(MaxDescriptionLength);
        RuleFor(c => c.Lineup).Must(l => l.Count <= MaxLineup).WithMessage($"At most {MaxLineup} artists.");
        RuleForEach(c => c.Lineup).MaximumLength(MaxArtistLength);
        RuleFor(c => c.CoverImageUrl).MaximumLength(500);
    }
}

public sealed class UpdateEventInfoHandler(IAppDbContext db, IFileStorage storage, ICatalogCacheInvalidator cache)
{
    public async Task<Result<OrganizerEvent>> HandleAsync(UpdateEventInfoCommand command, CancellationToken ct)
    {
        // Only covers uploaded through this event's presigned URL, never arbitrary URLs (same rule as avatars).
        if (command.CoverImageUrl is { } url && !storage.IsPublicUrlUnder(url, EventCoverKeys.PrefixFor(command.EventId)))
        {
            return EventErrors.CoverNotOwned;
        }

        var result = await OrganizerEvents.ChangeAsync(db, command.EventId, command.OrganizerId, command.Version, evt =>
        {
            evt.UpdateInfo(command.Title, command.Description.Trim(), command.Lineup, command.CoverImageUrl);
            return Task.FromResult(Result.Success());
        }, ct);

        // A live event's public page must show the new text now; the web's ISR copy refreshes through the outbox.
        if (result is { IsSuccess: true, Value.Status: EventStatus.Published })
        {
            await cache.InvalidateEventAsync(result.Value.Id, result.Value.Slug, ct);
        }

        return result;
    }
}

/// <summary>Draft only: category, venue and dates are frozen once published.</summary>
public sealed record UpdateEventScheduleCommand(
    Guid OrganizerId,
    Guid EventId,
    string Version,
    string Category,
    Guid VenueId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed class UpdateEventScheduleValidator : AbstractValidator<UpdateEventScheduleCommand>
{
    public UpdateEventScheduleValidator()
    {
        RuleFor(c => c.Version).Must(OrganizerEvents.IsVersion).WithMessage("Send the version you edited.");
        RuleFor(c => c.Category).Must(EventCategories.IsKnown).WithMessage("Unknown category.");
        RuleFor(c => c.EndsAt).GreaterThan(c => c.StartsAt).WithMessage("The event must end after it starts.");
    }
}

public sealed class UpdateEventScheduleHandler(IAppDbContext db)
{
    public Task<Result<OrganizerEvent>> HandleAsync(UpdateEventScheduleCommand command, CancellationToken ct) =>
        OrganizerEvents.ChangeAsync(db, command.EventId, command.OrganizerId, command.Version, async evt =>
        {
            if (await OrganizerEvents.LoadUsableVenueAsync(db, command.VenueId, command.OrganizerId, ct) is not { } venue)
            {
                return EventErrors.VenueNotFound;
            }

            evt.UpdateSchedule(command.Category, venue, command.StartsAt, command.EndsAt);
            return Result.Success();
        }, ct);
}

public sealed record CoverUploadCommand(Guid OrganizerId, Guid EventId, string ContentType, long Size);

public sealed class CoverUploadValidator : AbstractValidator<CoverUploadCommand>
{
    public const long MaxBytes = 5 * 1024 * 1024;

    // Raster formats only: SVG can carry script.
    public static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/webp"] = "webp",
        ["image/avif"] = "avif",
    };

    public CoverUploadValidator()
    {
        RuleFor(c => c.ContentType).Must(Extensions.ContainsKey).WithMessage("Use a JPEG, PNG, WebP or AVIF image.");
        RuleFor(c => c.Size).InclusiveBetween(1, MaxBytes).WithMessage("The image must be 5 MB or smaller.");
    }
}

public sealed class CoverUploadHandler(IAppDbContext db, IFileStorage storage)
{
    public async Task<Result<UploadTicket>> HandleAsync(CoverUploadCommand command, CancellationToken ct)
    {
        if (await OrganizerEvents.LoadOwnedAsync(db, command.EventId, command.OrganizerId, ct) is null)
        {
            return EventErrors.NotFound;
        }

        var key = $"{EventCoverKeys.PrefixFor(command.EventId)}{Guid.CreateVersion7():N}.{CoverUploadValidator.Extensions[command.ContentType]}";
        return storage.CreateUploadUrl(key, command.ContentType, command.Size);
    }
}
