namespace TicketRush.Infrastructure.Notifications;

/// <summary>The pause of the delivery loop while SMTP looks down: 5 s, doubling, up to the configured cap.</summary>
public static class EmailOutageBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromSeconds(5);

    /// <param name="current">The previous pause, or <see cref="TimeSpan.Zero"/> after a pass that saw no outage.</param>
    public static TimeSpan Next(TimeSpan current, TimeSpan max) =>
        current <= TimeSpan.Zero ? First : TimeSpan.FromTicks(Math.Min(current.Ticks * 2, max.Ticks));
}
