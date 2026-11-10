using System.Diagnostics.Metrics;
using TicketRush.Application.Features.Orders;

namespace TicketRush.Worker.Payments;

/// <summary>
/// Counters of the expiry sweep and the refund worker (spec 08), on the <c>TicketRush.Payments</c> meter (exported by the
/// <c>TicketRush.*</c> wildcard). Alert on <c>ticketrush.payments.refunds_parked</c> &gt; 0: money a person must send back.
/// </summary>
public sealed class PaymentWorkerMetrics
{
    public const string MeterName = "TicketRush.Payments";

    private readonly Counter<long> _expiry;
    private readonly Counter<long> _refunds;
    private readonly Counter<long> _sessions;
    private readonly Counter<long> _cancelledEvents;
    private long _parked;

    public PaymentWorkerMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _expiry = meter.CreateCounter<long>("ticketrush.orders.expiry", description: "Orders the expiry sweep looked at, by outcome.");
        _sessions = meter.CreateCounter<long>("ticketrush.payments.sessions_reconciled", description: "Stale Pending sessions of closed orders asked again, by outcome.");
        _cancelledEvents = meter.CreateCounter<long>("ticketrush.orders.cancelled_event_sweep", description: "Cancelled events the backstop pass settled, by outcome (settled|unchanged|error).");
        _refunds = meter.CreateCounter<long>("ticketrush.payments.refunds", description: "Refund tries, by target (order|session) and outcome.");
        meter.CreateObservableGauge(
            "ticketrush.payments.refunds_parked",
            () => Interlocked.Read(ref _parked),
            description: "Refunds parked after too many declines (or missing data); each one needs a person.");
    }

    /// <summary>Tag values: expired, fulfilled_late, refund_late, skipped_outage, unchanged.</summary>
    public void Expiry(OrderExpiryOutcome outcome) => _expiry.Add(1, Tag("outcome", ExpiryTag(outcome)));

    public void ExpiryFailed() => _expiry.Add(1, Tag("outcome", "error"));

    /// <summary>Tag values: closed, still_pending, fulfilled_late, refund_late, skipped_outage, unchanged.</summary>
    public void Session(OrderExpiryOutcome outcome) => _sessions.Add(1, Tag("outcome", ExpiryTag(outcome)));

    public void SessionFailed() => _sessions.Add(1, Tag("outcome", "error"));

    /// <summary>Tag values: refunded, declined, parked, skipped_outage, unchanged.</summary>
    public void Refund(RefundTarget target, RefundOutcome outcome) =>
        _refunds.Add(1, Tag("target", TargetTag(target)), Tag("outcome", RefundTag(outcome)));

    public void RefundFailed(RefundTarget target) => _refunds.Add(1, Tag("target", TargetTag(target)), Tag("outcome", "error"));

    /// <summary>Tag values: settled (the outbox message had not done it), unchanged, error.</summary>
    public void CancelledEventSweep(string outcome) => _cancelledEvents.Add(1, Tag("outcome", outcome));

    public void SetParked(long count) => Interlocked.Exchange(ref _parked, count);

    private static KeyValuePair<string, object?> Tag(string name, string value) => new(name, value);

    private static string TargetTag(RefundTarget target) => target == RefundTarget.Order ? "order" : "session";

    private static string ExpiryTag(OrderExpiryOutcome outcome) => outcome switch
    {
        OrderExpiryOutcome.Expired => "expired",
        OrderExpiryOutcome.FulfilledLate => "fulfilled_late",
        OrderExpiryOutcome.RefundLate => "refund_late",
        OrderExpiryOutcome.SkippedOutage => "skipped_outage",
        OrderExpiryOutcome.Closed => "closed",
        OrderExpiryOutcome.StillPending => "still_pending",
        _ => "unchanged",
    };

    private static string RefundTag(RefundOutcome outcome) => outcome switch
    {
        RefundOutcome.Refunded => "refunded",
        RefundOutcome.Declined => "declined",
        RefundOutcome.Parked => "parked",
        RefundOutcome.SkippedOutage => "skipped_outage",
        _ => "unchanged",
    };
}
