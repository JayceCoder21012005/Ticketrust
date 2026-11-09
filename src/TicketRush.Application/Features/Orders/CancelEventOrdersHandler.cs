using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Orders;

/// <summary>What one committed batch (or a whole run) of a cancelled event's order settlement changed.</summary>
public sealed record EventOrderSettlement(int Cancelled, int Expired, int RefundPending, int TicketsVoided)
{
    public static EventOrderSettlement None { get; } = new(0, 0, 0, 0);

    public int Orders => Cancelled + Expired + RefundPending;

    public EventOrderSettlement Add(EventOrderSettlement other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Cancelled + other.Cancelled, Expired + other.Expired, RefundPending + other.RefundPending, TicketsVoided + other.TicketsVoided);
    }
}

/// <summary>
/// Spec 08 "EventCancelled handler" (task B9): settles every order of a cancelled event, in batches of at most
/// <see cref="BatchSize"/> orders, each batch ONE transaction that first takes the event sale lock exclusively:
/// <list type="bullet">
/// <item><b>Paid</b> → <see cref="Order.RequestRefundForCancelledEvent"/> (RefundPending, <c>event_cancelled</c>, outbox
/// <see cref="RefundRequested"/>), every ticket of the order not yet voided → <see cref="Ticket.VoidForCancelledEvent"/>
/// (a checked-in one too), and the general-admission units go back with <see cref="ITierInventory.ReleaseAsync"/>. All of it
/// commits together, so the refund worker (which picks up RefundPending orders) never sees a refund owed on live tickets,
/// and <c>sold_count</c> stays equal to the tier's non-voided GA tickets.</item>
/// <item><b>Pending</b> (no payment session was ever opened) → <see cref="Order.Cancel"/>.</item>
/// <item><b>AwaitingPayment / PaymentFailed</b> → <see cref="Order.Expire"/>, never Cancelled: a session may have captured
/// money (a Cancelled order must never have taken any). Their sessions stay Pending; a late webhook, or the expiry sweep's
/// session reconcile once the grace is over, runs the fulfil path, which finds the event Cancelled and moves the order to
/// RefundPending <c>event_cancelled</c> (Expired → RefundPending is legal), so the money goes back.</item>
/// <item>RefundPending, Refunded, Cancelled and Expired orders are left as they are, so a re-run (redelivery, retry after a
/// failure) only does what is left; a batch that failed was rolled back whole.</item>
/// </list>
/// Nothing is done unless the event is Cancelled. Redis is not touched: selling is closed (the rooms are closed and the holds
/// released before this runs), so the <c>sold</c> set and <c>bought</c> counters of a cancelled event are never read again.
/// A concurrent change of an order or ticket (<c>xmin</c>) or a deadlock re-runs the batch from fresh reads, up to
/// <see cref="MaxConcurrencyRetries"/> times; then the exception surfaces (the outbox retries the message).
/// </summary>
public sealed partial class CancelEventOrdersHandler(
    IAppDbContext db,
    IEventSaleLock saleLock,
    ITierInventory tiers,
    IOptions<FulfilmentOptions> options,
    ILogger<CancelEventOrdersHandler> logger)
{
    public const int BatchSize = 500;

    public const int MaxConcurrencyRetries = 3;

    /// <param name="onBatchCommitted">Called after each committed batch (metrics), with what that batch changed.</param>
    /// <returns>Everything this run changed (<see cref="EventOrderSettlement.None"/> when nothing was left to do).</returns>
    public async Task<EventOrderSettlement> HandleAsync(Guid eventId, Action<EventOrderSettlement>? onBatchCommitted, CancellationToken ct)
    {
        var total = EventOrderSettlement.None;
        while (await SettleBatchAsync(eventId, ct) is { Orders: > 0 } batch)
        {
            LogBatch(eventId, batch.Cancelled, batch.Expired, batch.RefundPending, batch.TicketsVoided);
            onBatchCommitted?.Invoke(batch);
            total = total.Add(batch);
        }

        return total;
    }

    private async Task<EventOrderSettlement> SettleBatchAsync(Guid eventId, CancellationToken ct)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await db.InTransactionAsync(token => SettleBatchInTransactionAsync(eventId, token), options.Value.LockTimeout, ct);
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or DatabaseContentionException && retry < MaxConcurrencyRetries)
            {
                LogRetry(eventId, retry + 1, ex);
            }
            finally
            {
                // Rolled back or committed, the tracked rows are done with: the next batch (or retry) reads afresh.
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<EventOrderSettlement> SettleBatchInTransactionAsync(Guid eventId, CancellationToken ct)
    {
        // Lock order everywhere: event sale lock → buyer limit lock → GA tier rows → seats. Fulfilments in flight finish first.
        await saleLock.AcquireExclusiveAsync(eventId, ct);
        var status = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => (EventStatus?)e.Status).FirstOrDefaultAsync(ct);
        if (status != EventStatus.Cancelled)
        {
            LogNotCancelled(eventId, status);
            return EventOrderSettlement.None;
        }

        // ix_orders_event_id. Every order this returns leaves the set in this batch, so the loop ends.
        var orders = await db.Orders
            .Where(o => o.EventId == eventId && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment
                || o.Status == OrderStatus.PaymentFailed || o.Status == OrderStatus.Paid))
            .OrderBy(o => o.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
        var tickets = await LiveTicketsAsync(eventId, orders, ct);
        var settlement = orders.Aggregate(EventOrderSettlement.None, (sum, order) => sum.Add(Settle(order, tickets[order.Id])));
        await ReleaseGeneralAdmissionAsync(eventId, tickets.SelectMany(t => t), ct);
        await db.SaveChangesAsync(ct);
        return settlement;
    }

    /// <summary>The non-voided tickets of the batch's Paid orders, by order.</summary>
    private async Task<ILookup<Guid, Ticket>> LiveTicketsAsync(Guid eventId, IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var paid = orders.Where(o => o.Status == OrderStatus.Paid).Select(o => o.Id).ToList();
        if (paid.Count == 0)
        {
            return Array.Empty<Ticket>().ToLookup(t => t.OrderId);
        }

        // ix_tickets_order_id; the event filter is a guard (a ticket always belongs to its order's event).
        var live = await db.Tickets.Where(t => t.EventId == eventId && paid.Contains(t.OrderId) && t.Status != TicketStatus.Voided).ToListAsync(ct);
        return live.ToLookup(t => t.OrderId);
    }

    private static EventOrderSettlement Settle(Order order, IEnumerable<Ticket> liveTickets)
    {
        switch (order.Status)
        {
            case OrderStatus.Pending:
                order.Cancel();
                return EventOrderSettlement.None with { Cancelled = 1 };
            case OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed:
                order.Expire();
                return EventOrderSettlement.None with { Expired = 1 };
            default:
                order.RequestRefundForCancelledEvent();
                var voided = 0;
                foreach (var ticket in liveTickets)
                {
                    ticket.VoidForCancelledEvent();
                    voided++;
                }

                return new EventOrderSettlement(0, 0, 1, voided);
        }
    }

    /// <summary>
    /// One release per GA tier, in tier id order (the order fulfilments claim them in). A release that finds fewer units
    /// claimed than tickets voided means <c>sold_count</c> had already drifted: logged at Error for a person, and the
    /// settlement goes on, because blocking it would keep the buyers' money.
    /// </summary>
    private async Task ReleaseGeneralAdmissionAsync(Guid eventId, IEnumerable<Ticket> voided, CancellationToken ct)
    {
        foreach (var tier in voided.Where(t => t.SeatIndex is null).GroupBy(t => t.TierId).OrderBy(g => g.Key))
        {
            var quantity = tier.Count();
            if (!await tiers.ReleaseAsync(tier.Key, quantity, ct))
            {
                LogReleaseDrift(eventId, tier.Key, quantity);
            }
        }
    }

    [LoggerMessage(EventId = 8301, Level = LogLevel.Information, Message = "Cancelled event {EventId}: batch committed; {Cancelled} orders cancelled, {Expired} expired, {RefundPending} sent to refund, {TicketsVoided} tickets voided.")]
    private partial void LogBatch(Guid eventId, int cancelled, int expired, int refundPending, int ticketsVoided);

    [LoggerMessage(EventId = 8302, Level = LogLevel.Warning, Message = "Cancelled event {EventId}: a settlement batch collided with a concurrent change (retry {Retry}).")]
    private partial void LogRetry(Guid eventId, int retry, Exception exception);

    [LoggerMessage(EventId = 8303, Level = LogLevel.Warning, Message = "Event {EventId} is {Status}, not Cancelled: its orders are not settled.")]
    private partial void LogNotCancelled(Guid eventId, EventStatus? status);

    [LoggerMessage(EventId = 8304, Level = LogLevel.Error, Message = "Cancelled event {EventId}: tier {TierId} had fewer than {Quantity} units claimed while its tickets were voided; sold_count had drifted. NEEDS ATTENTION.")]
    private partial void LogReleaseDrift(Guid eventId, Guid tierId, int quantity);
}
