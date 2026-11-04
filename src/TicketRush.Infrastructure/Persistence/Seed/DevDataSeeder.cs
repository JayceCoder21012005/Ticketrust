using TicketRush.Application.Abstractions.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Infrastructure.Persistence.Seed;

/// <summary>
/// Idempotent development data. Every row is looked up by a natural key (email, venue name, event slug)
/// before it is created, so running the seeder twice never duplicates anything.
/// </summary>
public sealed class DevDataSeeder(
    AppDbContext db,
    UserManager<AppUser> userManager,
    RoleManager<AppRole> roleManager,
    IPasswordHasher<AppUser> passwordHasher,
    TimeProvider timeProvider,
    IAccessCodeHasher accessCodeHasher)
{
    public const string DevPassword = "Dev@12345";

    public static readonly IReadOnlyList<(string Email, string FullName, string Role)> Users =
    [
        ("customer@ticketrush.dev", "Nguyễn Văn Khách", Roles.Customer),
        ("organizer@ticketrush.dev", "Trần Thị Tổ Chức", Roles.Organizer),
        ("admin@ticketrush.dev", "Lê Quản Trị", Roles.Admin),
        ("gate@ticketrush.dev", "Phạm Soát Vé", Roles.GateStaff),
    ];

    public async Task SeedAsync(CancellationToken ct)
    {
        await SeedRolesAsync();
        await SeedUsersAsync();
        var organizer = await userManager.FindByEmailAsync("organizer@ticketrush.dev")
            ?? throw new InvalidOperationException("Organizer seed user is missing.");

        var venues = await SeedVenuesAsync(ct);
        await SeedEventsAsync(organizer.Id, venues, ct);
    }

    private async Task SeedRolesAsync()
    {
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                EnsureSucceeded(await roleManager.CreateAsync(new AppRole(role)));
            }
        }
    }

    private async Task SeedUsersAsync()
    {
        foreach (var (email, fullName, role) in Users)
        {
            if (await userManager.FindByEmailAsync(email) is not null)
            {
                continue;
            }

            var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = fullName, EmailConfirmed = true };
            // Hash directly so the seed doesn't depend on the (stricter) registration password policy.
            user.PasswordHash = passwordHasher.HashPassword(user, DevPassword);
            EnsureSucceeded(await userManager.CreateAsync(user));
            EnsureSucceeded(await userManager.AddToRoleAsync(user, role));
        }
    }

    private async Task<IReadOnlyDictionary<string, Venue>> SeedVenuesAsync(CancellationToken ct)
    {
        var wanted = SeedEventCatalog.Venues();
        var names = wanted.Select(v => v.Name).ToList();
        var existing = await db.Venues.Where(v => names.Contains(v.Name)).ToDictionaryAsync(v => v.Name, ct);

        foreach (var venue in wanted.Where(v => !existing.ContainsKey(v.Name)))
        {
            db.Venues.Add(venue);
            existing[venue.Name] = venue;
        }

        await db.SaveChangesAsync(ct);
        return existing;
    }

    private async Task SeedEventsAsync(Guid organizerId, IReadOnlyDictionary<string, Venue> venues, CancellationToken ct)
    {
        var catalog = new SeedEventCatalog(organizerId, timeProvider.GetUtcNow(), venues, accessCodeHasher.Hash(SeedEventCatalog.PresaleCode));

        var existingSlugs = await db.Events.Select(e => e.Slug).ToListAsync(ct);
        foreach (var evt in catalog.Build().Where(e => !existingSlugs.Contains(e.Slug)))
        {
            db.Events.Add(evt);
        }

        await db.SaveChangesAsync(ct);
        await SeedSeatsAsync(ct);
    }

    /// <summary>Looked up per event, so databases seeded before spec 04 get their seat maps too.</summary>
    private async Task SeedSeatsAsync(CancellationToken ct)
    {
        var seated = await db.Events
            .Include(e => e.Tiers)
            .Where(e => SeedEventCatalog.SeatedEventSlugs.Contains(e.Slug))
            .Where(e => !db.EventSeats.Any(seat => seat.EventId == e.Id))
            .ToListAsync(ct);

        foreach (var evt in seated)
        {
            db.EventSeats.AddRange(SeedEventCatalog.SeatsFor(evt));
        }

        await db.SaveChangesAsync(ct);
    }

    private static void EnsureSucceeded(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            throw new InvalidOperationException($"Seeding identity data failed: {errors}");
        }
    }
}
