using System.Globalization;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Venues;

namespace TicketRush.Infrastructure.Persistence.Seed;

/// <summary>
/// Development catalog: every status, every category, three cities, and sales opening at useful moments:
/// several are on sale, one opens 5 minutes after seeding (waiting room), one 20 minutes after (pre-queue CTA).
/// </summary>
internal sealed class SeedEventCatalog(Guid organizerId, DateTimeOffset now, IReadOnlyDictionary<string, Venue> venues)
{
    public const string OpeningSoonSlug = "ha-anh-tuan-chan-troi-ruc-ro";
    public const int SeatsPerSeatedEvent = 3 * 10 * 20;

    private const string PhuTho = "Nhà thi đấu Phú Thọ";
    private const string MyDinh = "Sân vận động Mỹ Đình";
    private const string TienSon = "Cung thể thao Tiên Sơn";

    public static readonly IReadOnlyList<string> SeatedEventSlugs = ["son-tung-mtp-live", OpeningSoonSlug];

    public static IReadOnlyList<Venue> Venues() =>
    [
        Venue.Create(PhuTho, "1 Lữ Gia, Phường 15, Quận 11", "Hồ Chí Minh", "https://maps.google.com/?q=Nha+thi+dau+Phu+Tho", 8000),
        Venue.Create(MyDinh, "Lê Đức Thọ, Nam Từ Liêm", "Hà Nội", "https://maps.google.com/?q=San+van+dong+My+Dinh", 40000),
        Venue.Create(TienSon, "Phan Đăng Lưu, Hải Châu", "Đà Nẵng", "https://maps.google.com/?q=Cung+the+thao+Tien+Son", 6000),
    ];

    public IEnumerable<Event> Build()
    {
        yield return Published("son-tung-mtp-live", "Sơn Tùng M-TP Live", "concert", MyDinh, 30, Seated(3_000_000, 1_500_000, 800_000),
            "Đêm nhạc hoành tráng nhất năm với sân khấu 360° và dàn khách mời bí mật.", ["Sơn Tùng M-TP"],
            evt => evt.AddPhase("Mở bán", now.AddDays(-1), now.AddDays(29), queueEnabled: true, maxTicketsPerUser: 4));

        yield return Published(OpeningSoonSlug, "Hà Anh Tuấn - Chân Trời Rực Rỡ", "concert", PhuTho, 45, Seated(2_500_000, 1_200_000, 600_000),
            "Live concert cùng dàn nhạc giao hưởng 60 nhạc công.", ["Hà Anh Tuấn", "Phương Linh"],
            evt => evt.AddPhase("Mở bán", now.AddMinutes(5), now.AddDays(44), queueEnabled: true, maxTicketsPerUser: 4));

        yield return Published("vu-cat-tuong-inner-space", "Vũ Cát Tường - Inner Space", "concert", MyDinh, 35, Ga(("GA", 900_000, 3000), ("Fanzone", 1_600_000, 800)),
            "Hành trình âm nhạc mới nhất của Vũ Cát Tường.", ["Vũ Cát Tường"],
            evt => evt.AddPhase("Mở bán", now.AddMinutes(20), now.AddDays(34), queueEnabled: true, maxTicketsPerUser: 4));

        yield return Published("den-vau-show-2026", "Đen Vâu - Show Của Đen", "concert", PhuTho, 60, Ga(("GA", 700_000, 4000), ("VIP", 2_000_000, 500)),
            "Rap và những người bạn.", ["Đen Vâu", "Hậu Vi", "Linh Cáo"],
            evt =>
            {
                evt.AddPhase("Presale", now.AddDays(14), now.AddDays(15), queueEnabled: true, maxTicketsPerUser: 2, accessCodeHash: "seed-presale");
                evt.AddPhase("Mở bán", now.AddDays(16), now.AddDays(59), queueEnabled: true, maxTicketsPerUser: 4);
            });

        yield return Published("my-tam-tri-ky", "Mỹ Tâm - Tri Kỷ", "concert", TienSon, 25, Ga(("GA", 800_000, 3000), ("VIP", 2_200_000, 600)),
            "Đêm nhạc tri ân khán giả miền Trung.", ["Mỹ Tâm"],
            evt => evt.AddPhase("Mở bán", now.AddDays(-3), now.AddDays(24), queueEnabled: true, maxTicketsPerUser: 4));

        yield return Published("hozo-music-festival-2026", "HOZO Music Festival 2026", "festival", PhuTho, 40, Ga(("1 ngày", 500_000, 5000), ("2 ngày", 850_000, 3000)),
            "Lễ hội âm nhạc đa văn hoá ngoài trời, 2 ngày, 3 sân khấu.", ["Ngọt", "Cá Hồi Hoang", "Chillies"],
            evt => evt.AddPhase("Mở bán", now.AddDays(2), now.AddDays(39), queueEnabled: false, maxTicketsPerUser: 6));

        yield return Published("kich-idecaf-ngoi-nha-hanh-phuc", "Kịch IDECAF - Ngôi Nhà Hạnh Phúc", "theater", PhuTho, 12, Ga(("Ghế thường", 350_000, 400), ("Ghế đẹp", 550_000, 150)),
            "Vở hài kịch gia đình kinh điển của sân khấu IDECAF.", ["NSƯT Thành Lộc", "Hữu Châu"],
            evt => evt.AddPhase("Mở bán", now.AddDays(-5), now.AddDays(11), queueEnabled: false, maxTicketsPerUser: 8));

        yield return Published("ballet-ho-thien-nga", "Ballet Hồ Thiên Nga", "theater", MyDinh, 28, Ga(("Hạng B", 600_000, 800), ("Hạng A", 1_200_000, 300)),
            "Kiệt tác ballet của Tchaikovsky do Nhà hát Nhạc Vũ Kịch Việt Nam trình diễn.", ["Nhà hát Nhạc Vũ Kịch Việt Nam"],
            evt => evt.AddPhase("Mở bán", now.AddHours(6), now.AddDays(27), queueEnabled: false, maxTicketsPerUser: 6));

        yield return Published("v-league-ha-noi-vs-cong-an", "V.League: Hà Nội FC vs Công An Hà Nội", "sports", MyDinh, 9, Ga(("Khán đài B", 150_000, 10000), ("Khán đài A", 300_000, 5000)),
            "Trận derby thủ đô vòng 10 V.League 2026/27.", ["Hà Nội FC", "Công An Hà Nội"],
            evt => evt.AddPhase("Mở bán", now.AddDays(-2), now.AddDays(8), queueEnabled: false, maxTicketsPerUser: 8));

        yield return Published("marathon-da-nang-2026", "Marathon Quốc tế Đà Nẵng 2026", "sports", TienSon, 50, Ga(("10 km", 450_000, 3000), ("21 km", 650_000, 2000), ("42 km", 850_000, 1000)),
            "Cung đường chạy ven biển đẹp nhất Việt Nam.", [],
            evt => evt.AddPhase("Mở bán", now.AddDays(20), now.AddDays(49), queueEnabled: false, maxTicketsPerUser: 2));

        yield return Published("saigon-teu-cuoi-xuyen-dem", "Saigon Tếu - Cười Xuyên Đêm", "comedy", PhuTho, 18, Ga(("Phổ thông", 250_000, 500)),
            "Đêm stand-up comedy với 6 nghệ sĩ hài độc thoại.", ["Uy Lê", "Phương Nam"],
            evt => evt.AddPhase("Mở bán", now.AddDays(-4), now.AddDays(17), queueEnabled: false, maxTicketsPerUser: 6));

        yield return Published("workshop-thiet-ke-trai-nghiem", "Workshop Thiết kế Trải nghiệm Sự kiện", "workshop", TienSon, 7, Ga(("Vé tham dự", 400_000, 120)),
            "Một buổi chiều thực hành thiết kế hành trình khách tham dự.", [],
            evt => evt.AddPhase("Mở bán", now.AddDays(-10), now.AddDays(6), queueEnabled: false, maxTicketsPerUser: 2));

        yield return DraftEvent();
        yield return EndedEvent();
        yield return CancelledEvent();
    }

    /// <summary>Three sections × 10 rows × 20 seats; section A is the first seated tier (VIP), B and C the second.</summary>
    public static IEnumerable<EventSeat> SeatsFor(Event evt)
    {
        var seated = evt.Tiers.Where(t => t.Kind == TicketKind.Seated).ToList();
        var vip = seated[0].Id;
        var standard = seated.Count > 1 ? seated[1].Id : vip;
        string[] sections = ["A", "B", "C"];

        var index = 0;
        for (var s = 0; s < sections.Length; s++)
        {
            for (var row = 1; row <= 10; row++)
            {
                for (var number = 1; number <= 20; number++)
                {
                    var x = ((s * 22) + number) * 10;
                    var tier = s == 0 ? vip : standard;
                    yield return EventSeat.Create(evt.Id, index++, sections[s], row.ToString(CultureInfo.InvariantCulture), number.ToString(CultureInfo.InvariantCulture), x, row * 12, tier);
                }
            }
        }
    }

    private Event DraftEvent()
    {
        var evt = Draft("rock-storm-2026", "Rock Storm 2026", "festival", MyDinh, now.AddDays(90), Ga(("GA", 900_000, 8000)));
        evt.AddPhase("Mở bán", now.AddDays(30), now.AddDays(89), queueEnabled: false, maxTicketsPerUser: 6);
        return evt;
    }

    private Event EndedEvent()
    {
        var evt = Draft("jazz-night-saigon", "Jazz Night Saigon", "concert", PhuTho, now.AddDays(-7), Ga(("GA", 500_000, 500)));
        evt.AddPhase("Mở bán", now.AddDays(-30), now.AddDays(-8), queueEnabled: false, maxTicketsPerUser: 4);
        evt.Publish();
        evt.MarkEnded();
        return evt;
    }

    private Event CancelledEvent()
    {
        var evt = Draft("monsoon-festival-2026", "Monsoon Festival 2026", "festival", MyDinh, now.AddDays(20), Ga(("GA", 600_000, 6000)));
        evt.AddPhase("Mở bán", now.AddDays(2), now.AddDays(19), queueEnabled: true, maxTicketsPerUser: 4);
        evt.Publish();
        evt.Cancel();
        return evt;
    }

    private Event Published(
        string slug,
        string title,
        string category,
        string venue,
        int days,
        Action<Event> tiers,
        string description,
        string[] lineup,
        Action<Event> phases)
    {
        var evt = Draft(slug, title, category, venue, EveningOf(now.AddDays(days)), tiers);
        evt.UpdateDetails(description, lineup, null);
        phases(evt);
        evt.Publish();
        return evt;
    }

    private Event Draft(string slug, string title, string category, string venue, DateTimeOffset startsAt, Action<Event> tiers)
    {
        var evt = Event.CreateDraft(organizerId, slug, title, category, venues[venue], startsAt, startsAt.AddHours(3));
        tiers(evt);
        return evt;
    }

    /// <summary>19:30 Vietnam time on the given day, so cards don't show odd seed-time minutes.</summary>
    private static DateTimeOffset EveningOf(DateTimeOffset day)
    {
        var local = day.ToOffset(TimeSpan.FromHours(7));
        return new DateTimeOffset(local.Year, local.Month, local.Day, 19, 30, 0, TimeSpan.FromHours(7)).ToUniversalTime();
    }

    private static Action<Event> Seated(long vip, long standard, long ga) => evt =>
    {
        evt.AddTier("VIP", new Money(vip), TicketKind.Seated, null, "#e11d48");
        evt.AddTier("Standard", new Money(standard), TicketKind.Seated, null, "#2563eb");
        evt.AddTier("GA", new Money(ga), TicketKind.GeneralAdmission, 2000, "#16a34a");
    };

    private static Action<Event> Ga(params (string Name, long Price, int Capacity)[] tiers) => evt =>
    {
        string[] colors = ["#16a34a", "#d97706", "#7c3aed"];
        for (var i = 0; i < tiers.Length; i++)
        {
            evt.AddTier(tiers[i].Name, new Money(tiers[i].Price), TicketKind.GeneralAdmission, tiers[i].Capacity, colors[i % colors.Length]);
        }
    };
}
