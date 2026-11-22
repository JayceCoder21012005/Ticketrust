using TicketRush.Application.Abstractions.Dashboard;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Dashboard;

public static class DashboardErrors
{
    /// <summary>Also for another organizer's event, so ids cannot be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("dashboard.not_found", "Event not found.");

    public static Error InvalidRange(string message) =>
        Error.Validation("dashboard.invalid_range", message, new Dictionary<string, string[]> { ["range"] = [message] });
}

/// <param name="Capacity">Seats of the tier, or its general-admission capacity.</param>
public sealed record DashboardTier(Guid TierId, string Name, string Color, long Price, int Capacity, long Sold, int Held, int Remaining, long Revenue);

public sealed record DashboardFunnel(long QueueJoined, long Admitted, long Held, long OrdersCreated, long Paid);

/// <param name="AdmissionRate">Admissions per second (EMA, summed over the event's phases).</param>
public sealed record DashboardQueue(long Waiting, long ActiveShoppers, double AdmissionRate);

/// <summary>
/// One second of the dashboard (spec 15): <c>{ at, sold, revenue, tiers[], funnel, queue }</c> plus the last paid orders. Sold and
/// revenue count primary sales only.
/// </summary>
public sealed record DashboardSnapshot(
    Guid EventId,
    DateTimeOffset At,
    long Sold,
    long Revenue,
    string Currency,
    long Remaining,
    IReadOnlyList<DashboardTier> Tiers,
    DashboardFunnel Funnel,
    DashboardQueue Queue,
    IReadOnlyList<RecentOrder> RecentOrders);

public sealed record SalesPoint(DateTimeOffset At, Guid TierId, int Tickets, long Revenue);

/// <param name="Bucket"><c>1m</c> or <c>1h</c>.</param>
public sealed record SalesSeries(string Bucket, DateTimeOffset From, DateTimeOffset To, IReadOnlyList<SalesPoint> Points);

public sealed record EventOrderItem(Guid OrderId, string BuyerName, string BuyerEmail, int Tickets, long Total, OrderStatus Status, DateTimeOffset PaidAt);

public sealed record EventOrderPage(IReadOnlyList<EventOrderItem> Items, string? NextCursor);

/// <summary>One attendee row of the CSV export: one ticket that is not voided.</summary>
public sealed record AttendeeRow(
    Guid OrderId,
    string BuyerName,
    string BuyerEmail,
    string HolderName,
    string TierName,
    string? Section,
    string? Row,
    string? Number,
    DateTimeOffset? PaidAt,
    bool CheckedIn,
    DateTimeOffset? CheckedInAt);

/// <summary>Streams an event's attendees from the database, never all in memory (spec 15).</summary>
public interface IAttendeeExport
{
    IAsyncEnumerable<AttendeeRow> StreamAsync(Guid eventId, CancellationToken ct);
}

/// <summary>Sends a snapshot to the organizers watching an event (SignalR group <c>org:{eventId}</c>).</summary>
public interface IDashboardPublisher
{
    Task PublishAsync(DashboardSnapshot snapshot, CancellationToken ct);
}
