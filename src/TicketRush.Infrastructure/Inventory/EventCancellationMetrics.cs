using System.Diagnostics.Metrics;
using TicketRush.Application.Features.Orders;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>
/// What cancelled events' order settlements changed (spec 08, B9), per committed batch, on the <c>TicketRush.Payments</c>
/// meter (exported by the <c>TicketRush.*</c> wildcard). Failures surface as the outbox's own
/// <c>ticketrush.outbox.failed</c> / <c>dead_lettered</c> for <c>EventCancelled</c> plus an Error log.
/// </summary>
public sealed class EventCancellationMetrics
{
    public const string MeterName = "TicketRush.Payments";

    private readonly Counter<long> _orders;
    private readonly Counter<long> _tickets;

    public EventCancellationMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _orders = meter.CreateCounter<long>("ticketrush.orders.event_cancelled", description: "Orders of cancelled events settled, by outcome (cancelled|expired|refund_pending).");
        _tickets = meter.CreateCounter<long>("ticketrush.tickets.voided", description: "Tickets voided, by reason.");
    }

    public void Record(EventOrderSettlement batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Add(_orders, batch.Cancelled, "outcome", "cancelled");
        Add(_orders, batch.RefundPending, "outcome", "refund_pending");
        Add(_tickets, batch.TicketsVoided, "reason", "event_cancelled");
    }

    private static void Add(Counter<long> counter, int value, string tag, string tagValue)
    {
        if (value > 0)
        {
            counter.Add(value, new KeyValuePair<string, object?>(tag, tagValue));
        }
    }
}
