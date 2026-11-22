using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.LoadTest;

/// <summary><c>LoadTest:*</c>. Read only when the host runs in the <c>LoadTest</c> environment.</summary>
public sealed class LoadTestOptions
{
    public const string SectionName = "LoadTest";

    public const string EnvironmentName = "LoadTest";

    /// <summary>The shared secret the seed request must send in <c>X-LoadTest-Key</c>.</summary>
    [Required]
    [MinLength(32)]
    public string Key { get; set; } = string.Empty;
}

/// <param name="Users">Buyers to create, each with a pre-issued access token (no password: logging in would measure password hashing).</param>
/// <param name="Seats">Seats of the seated tiers; the first <paramref name="HotSeats"/> are the ones a scenario makes buyers fight over.</param>
/// <param name="OpensInSeconds">0 opens the sale now; more leaves time for a pre-queue (scenario 01).</param>
public sealed record LoadTestSeedRequest(
    int Users = 100,
    int Seats = 5000,
    int GaTiers = 2,
    int GaCapacity = 2000,
    int HotSeats = 50,
    int OpensInSeconds = 0,
    bool QueueEnabled = true,
    int MaxTicketsPerUser = 4)
{
    public const int MaxUsers = 50_000;
    public const int MaxSeats = 20_000;

    public string? Problem() =>
        Users is < 1 or > MaxUsers ? $"users must be 1..{MaxUsers}"
        : Seats is < 0 or > MaxSeats ? $"seats must be 0..{MaxSeats}"
        : GaTiers is < 0 or > 5 ? "gaTiers must be 0..5"
        : Seats == 0 && GaTiers == 0 ? "nothing to sell"
        : GaCapacity is < 1 or > 1_000_000 ? "gaCapacity must be 1..1000000"
        : HotSeats < 0 || HotSeats > Seats ? "hotSeats must be 0..seats"
        : OpensInSeconds is < 0 or > 86_400 ? "opensInSeconds must be 0..86400"
        : MaxTicketsPerUser is < 1 or > 10 ? "maxTicketsPerUser must be 1..10"
        : null;
}

public sealed record LoadTestBuyer(Guid UserId, string Token);

public sealed record LoadTestSeed(
    Guid EventId,
    string Slug,
    Guid PhaseId,
    DateTimeOffset OpensAt,
    Guid? SeatedTierId,
    IReadOnlyList<Guid> GaTierIds,
    int Seats,
    int HotSeats,
    int MaxTicketsPerUser,
    IReadOnlyList<LoadTestBuyer> Buyers);

/// <summary>
/// Fixtures for the spec 17 scenarios: a fresh published event (its own slug, so runs never share inventory) with a seat map of
/// 20-seat rows and general-admission tiers, and buyers with tokens. Users are inserted in bulk; Identity's per-user calls would
/// take minutes for tens of thousands.
/// </summary>
public sealed class LoadTestSeeder(AppDbContext db, IAccessTokenIssuer tokens, TimeProvider time)
{
    private const string OrganizerEmail = "loadtest-organizer@ticketrush.test";
    private const string OrganizerNormalized = "LOADTEST-ORGANIZER@TICKETRUSH.TEST";
    private const string VenueName = "LoadTest Arena";
    private const int SeatsPerRow = 20;
    private const int RowsPerSection = 25;

    public async Task<LoadTestSeed> SeedAsync(LoadTestSeedRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var run = $"{now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var customerRole = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync(ct);
        var organizerId = await OrganizerAsync(ct);
        var venue = await db.Venues.FirstOrDefaultAsync(v => v.Name == VenueName, ct);
        if (venue is null)
        {
            venue = Venue.Create(VenueName, "127.0.0.1", "Localhost", null, 100_000);
            db.Venues.Add(venue);
        }

        var opensAt = now.AddSeconds(request.OpensInSeconds);
        var evt = Event.CreateDraft(organizerId, $"loadtest-{run}", $"Load test {run}", "concert", venue, now.AddDays(7), now.AddDays(7).AddHours(3));
        var seated = request.Seats > 0 ? evt.AddTier("Seated", new Money(1_000_000), TicketKind.Seated, null, "#2563eb") : null;
        var ga = Enumerable.Range(1, request.GaTiers)
            .Select(i => evt.AddTier($"GA {i}", new Money(500_000), TicketKind.GeneralAdmission, request.GaCapacity, "#16a34a"))
            .ToList();
        var phase = evt.AddPhase("Mở bán", opensAt, now.AddDays(6), request.QueueEnabled, request.MaxTicketsPerUser);
        evt.Publish();
        db.Events.Add(evt);
        if (seated is not null)
        {
            db.EventSeats.AddRange(SeatsFor(evt.Id, seated.Id, request.Seats));
        }

        var buyers = NewBuyers(request.Users, run, customerRole);
        await db.SaveChangesAsync(ct);

        return new LoadTestSeed(
            evt.Id,
            evt.Slug,
            phase.Id,
            opensAt,
            seated?.Id,
            ga.ConvertAll(t => t.Id),
            request.Seats,
            request.HotSeats,
            request.MaxTicketsPerUser,
            buyers.ConvertAll(b => new LoadTestBuyer(b.Id, tokens.Issue(new UserAccount(b.Id, b.Email!, b.FullName, null, null, true, [Roles.Customer])).Token)));
    }

    /// <summary>Sections of 25 rows × 20 seats, laid out left to right; index order is section, row, seat (so the hot seats are the front rows of section A).</summary>
    private static IEnumerable<EventSeat> SeatsFor(Guid eventId, Guid tierId, int count)
    {
        var perSection = SeatsPerRow * RowsPerSection;
        for (var index = 0; index < count; index++)
        {
            var section = index / perSection;
            var row = (index % perSection) / SeatsPerRow;
            var number = index % SeatsPerRow;
            var name = section < 26 ? ((char)('A' + section)).ToString() : $"S{section + 1}";
            yield return EventSeat.Create(
                eventId, index, name, (row + 1).ToString(CultureInfo.InvariantCulture), (number + 1).ToString(CultureInfo.InvariantCulture),
                ((section * (SeatsPerRow + 2)) + number) * 10, row * 12, tierId);
        }
    }

    private List<AppUser> NewBuyers(int count, string run, Guid customerRole)
    {
        var users = new List<AppUser>(count);
        for (var i = 0; i < count; i++)
        {
            var email = $"lt-{run}-{i:D5}@ticketrush.test";
            var user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                Email = email,
                NormalizedEmail = email.ToUpperInvariant(),
                UserName = email,
                NormalizedUserName = email.ToUpperInvariant(),
                FullName = $"Load Buyer {i:D5}",
                EmailConfirmed = true,
                SecurityStamp = Guid.NewGuid().ToString("N"),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
            };
            users.Add(user);
            db.Users.Add(user);
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = customerRole });
        }

        return users;
    }

    private async Task<Guid> OrganizerAsync(CancellationToken ct)
    {
        if (await db.Users.Where(u => u.NormalizedEmail == OrganizerNormalized).Select(u => (Guid?)u.Id).SingleOrDefaultAsync(ct) is { } id)
        {
            return id;
        }

        var organizer = new AppUser
        {
            Id = Guid.CreateVersion7(),
            Email = OrganizerEmail,
            NormalizedEmail = OrganizerNormalized,
            UserName = OrganizerEmail,
            NormalizedUserName = OrganizerNormalized,
            FullName = "Load Test Organizer",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };
        db.Users.Add(organizer);
        var role = await db.Roles.Where(r => r.Name == Roles.Organizer).Select(r => r.Id).SingleAsync(ct);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = organizer.Id, RoleId = role });
        await db.SaveChangesAsync(ct);
        return organizer.Id;
    }
}
