using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Venues;

namespace TicketRush.Application.Features.Organizer.Venues;

public sealed class ListVenuesHandler(IAppDbContext db)
{
    private static readonly StringComparer Vietnamese = StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), ignoreCase: true);

    public async Task<IReadOnlyList<VenueItem>> HandleAsync(Guid organizerId, CancellationToken ct)
    {
        var venues = await db.Venues
            .AsNoTracking()
            .Where(v => v.OrganizerId == null || v.OrganizerId == organizerId)
            .Select(v => new VenueItem(v.Id, v.Name, v.Address, v.City, v.MapUrl, v.Capacity, v.OrganizerId == null))
            .ToListAsync(ct);

        return venues.OrderBy(v => v.Name, Vietnamese).ToList();
    }
}

public sealed record CreateVenueCommand(Guid OrganizerId, string Name, string Address, string City, string? MapUrl, int Capacity);

public sealed class CreateVenueValidator : AbstractValidator<CreateVenueCommand>
{
    public CreateVenueValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Address).NotEmpty().MaximumLength(300);
        RuleFor(c => c.City).NotEmpty().MaximumLength(100);
        RuleFor(c => c.Capacity).InclusiveBetween(1, 200_000);
        RuleFor(c => c.MapUrl)
            .MaximumLength(500)
            .Must(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            .When(c => !string.IsNullOrWhiteSpace(c.MapUrl))
            .WithMessage("Use an http(s) link.");
    }
}

public sealed class CreateVenueHandler(IAppDbContext db)
{
    public async Task<VenueItem> HandleAsync(CreateVenueCommand command, CancellationToken ct)
    {
        var mapUrl = string.IsNullOrWhiteSpace(command.MapUrl) ? null : command.MapUrl.Trim();
        var venue = Venue.Create(command.Name, command.Address, command.City, mapUrl, command.Capacity, organizerId: command.OrganizerId);
        db.Venues.Add(venue);
        await db.SaveChangesAsync(ct);
        return new VenueItem(venue.Id, venue.Name, venue.Address, venue.City, venue.MapUrl, venue.Capacity, Shared: false);
    }
}
