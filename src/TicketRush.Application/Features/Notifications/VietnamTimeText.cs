using System.Globalization;

namespace TicketRush.Application.Features.Notifications;

/// <summary>
/// An instant as the reader sees it in notices and emails (<c>20:00, 15/11/2026</c>, the <c>startsAtText</c> member of the
/// templates). Vietnam time has had no daylight saving since 1975, so a fixed +07:00 is <c>Asia/Ho_Chi_Minh</c> without
/// depending on the host's time zone database; the format is culture independent for the same reason.
/// </summary>
public static class VietnamTimeText
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static string Format(DateTimeOffset instant) =>
        instant.ToOffset(Offset).ToString("HH:mm, dd/MM/yyyy", CultureInfo.InvariantCulture);
}
