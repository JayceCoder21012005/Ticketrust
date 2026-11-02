using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
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
    TimeProvider timeProvider)
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
        Venue[] wanted =
        [
            Venue.Create("Nhà thi đấu Phú Thọ", "1 Lữ Gia, Phường 15, Quận 11", "Hồ Chí Minh", "https://maps.google.com/?q=Nha+thi+dau+Phu+Tho", 8000),
            Venue.Create("Sân vận động Mỹ Đình", "Lê Đức Thọ, Nam Từ Liêm", "Hà Nội", "https://maps.google.com/?q=San+van+dong+My+Dinh", 40000),
        ];

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
        var now = timeProvider.GetUtcNow();
        var hcm = venues["Nhà thi đấu Phú Thọ"].Id;
        var hanoi = venues["Sân vận động Mỹ Đình"].Id;
        var catalog = new SeedEventCatalog(organizerId, now, hcm, hanoi);

        var existingSlugs = await db.Events.Select(e => e.Slug).ToListAsync(ct);
        foreach (var evt in catalog.Build().Where(e => !existingSlugs.Contains(e.Slug)))
        {
            db.Events.Add(evt);
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

/// <summary>Six events that cover every status, plus one sale opening 5 minutes after seeding (waiting room).</summary>
internal sealed class SeedEventCatalog(Guid organizerId, DateTimeOffset now, Guid hcmVenue, Guid hanoiVenue)
{
    public const string OpeningSoonSlug = "ha-anh-tuan-chan-troi-ruc-ro";

    public IEnumerable<Event> Build()
    {
        yield return OnSaleNow();
        yield return OpeningSoon();
        yield return UpcomingSale();
        yield return DraftEvent();
        yield return EndedEvent();
        yield return CancelledEvent();
    }

    private Event OnSaleNow()
    {
        var evt = Draft("son-tung-mtp-live", "Sơn Tùng M-TP Live", "concert", hanoiVenue, now.AddDays(30));
        evt.UpdateDetails("Đêm nhạc hoành tráng nhất năm.", ["Sơn Tùng M-TP"], null);
        evt.AddPhase("Mở bán", now.AddDays(-1), now.AddDays(29), queueEnabled: true, maxTicketsPerUser: 4);
        evt.Publish();
        return evt;
    }

    private Event OpeningSoon()
    {
        var evt = Draft(OpeningSoonSlug, "Hà Anh Tuấn - Chân Trời Rực Rỡ", "concert", hcmVenue, now.AddDays(45));
        evt.UpdateDetails("Live concert với dàn nhạc giao hưởng.", ["Hà Anh Tuấn"], null);
        evt.AddPhase("Mở bán", now.AddMinutes(5), now.AddDays(44), queueEnabled: true, maxTicketsPerUser: 4);
        evt.Publish();
        return evt;
    }

    private Event UpcomingSale()
    {
        var evt = Draft("den-vau-show-2026", "Đen Vâu - Show Của Đen", "concert", hcmVenue, now.AddDays(60));
        evt.UpdateDetails("Rap và những người bạn.", ["Đen Vâu"], null);
        evt.AddPhase("Presale", now.AddDays(14), now.AddDays(15), queueEnabled: true, maxTicketsPerUser: 2, accessCodeHash: null);
        evt.AddPhase("Mở bán", now.AddDays(16), now.AddDays(59), queueEnabled: true, maxTicketsPerUser: 4);
        evt.Publish();
        return evt;
    }

    private Event DraftEvent()
    {
        var evt = Draft("rock-storm-2026", "Rock Storm 2026", "festival", hanoiVenue, now.AddDays(90));
        evt.AddPhase("Mở bán", now.AddDays(30), now.AddDays(89), queueEnabled: false, maxTicketsPerUser: 6);
        return evt;
    }

    private Event EndedEvent()
    {
        var evt = Draft("jazz-night-saigon", "Jazz Night Saigon", "jazz", hcmVenue, now.AddDays(-7));
        evt.AddPhase("Mở bán", now.AddDays(-30), now.AddDays(-8), queueEnabled: false, maxTicketsPerUser: 4);
        evt.Publish();
        evt.MarkEnded();
        return evt;
    }

    private Event CancelledEvent()
    {
        var evt = Draft("monsoon-festival-2026", "Monsoon Festival 2026", "festival", hanoiVenue, now.AddDays(20));
        evt.AddPhase("Mở bán", now.AddDays(2), now.AddDays(19), queueEnabled: true, maxTicketsPerUser: 4);
        evt.Publish();
        evt.Cancel();
        return evt;
    }

    private Event Draft(string slug, string title, string category, Guid venueId, DateTimeOffset startsAt)
    {
        var evt = Event.CreateDraft(organizerId, slug, title, category, venueId, startsAt, startsAt.AddHours(4));
        evt.AddTier("VIP", new Money(3_000_000), TicketKind.Seated, null, "#e11d48");
        evt.AddTier("Standard", new Money(1_500_000), TicketKind.Seated, null, "#2563eb");
        evt.AddTier("GA", new Money(800_000), TicketKind.GeneralAdmission, 2000, "#16a34a");
        return evt;
    }
}
