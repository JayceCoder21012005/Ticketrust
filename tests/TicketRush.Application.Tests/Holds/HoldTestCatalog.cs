using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;

namespace TicketRush.Application.Tests.Holds;

/// <summary>Section A: rows 1-3 × seats 1-10 of tier Seated (index = row * 10 + seat - 1), plus GA tier.</summary>
public static class HoldTestCatalog
{
    public static readonly Guid EventId = Guid.NewGuid();
    public static readonly Guid Seated = Guid.NewGuid();
    public static readonly Guid Other = Guid.NewGuid();
    public static readonly Guid Ga = Guid.NewGuid();
    public static readonly Guid Phase = Guid.NewGuid();

    public static HoldCatalogEntry Build(DateTimeOffset now, int maxPerUser = 4, IEnumerable<CatalogSeat>? extraSeats = null)
    {
        var seats = new List<CatalogSeat>();
        for (var row = 0; row < 3; row++)
        {
            for (var seat = 0; seat < 10; seat++)
            {
                // Seat 5/6 of each row is closest to the stage centre (x = 500).
                seats.Add(new CatalogSeat(row * 10 + seat, "A", (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), (seat + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), 380 + seat * 24, 100 + row * 28, Seated));
            }
        }

        seats.AddRange(extraSeats ?? []);
        return new HoldCatalogEntry(
            EventId,
            "su-kien",
            seats,
            [
                new CatalogTier(Seated, "VIP", 1_500_000, TicketKind.Seated, null),
                new CatalogTier(Other, "Thường", 600_000, TicketKind.Seated, null),
                new CatalogTier(Ga, "Đứng", 500_000, TicketKind.GeneralAdmission, 100),
            ],
            [new CatalogPhase(Phase, now.AddHours(-1), now.AddHours(2), maxPerUser)]);
    }
}
