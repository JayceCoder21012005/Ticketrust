namespace TicketRush.Domain.Dashboard;

/// <summary>
/// One row of <c>sales_per_minute</c> (spec 15): tickets and revenue of one tier of one event paid within one UTC minute. Written
/// only by the <c>OrderPaid</c> consumer with <c>INSERT … ON CONFLICT DO UPDATE</c>; with <c>orders</c> it is the dashboard's truth.
/// </summary>
public sealed class SalesMinute
{
    // Required by EF Core.
    private SalesMinute()
    {
    }

    public Guid EventId { get; private set; }

    /// <summary>The minute's start (UTC, seconds zeroed).</summary>
    public DateTimeOffset Minute { get; private set; }

    public Guid TierId { get; private set; }

    public int Tickets { get; private set; }

    /// <summary>Whole VND.</summary>
    public long Revenue { get; private set; }

    public static DateTimeOffset MinuteOf(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}
