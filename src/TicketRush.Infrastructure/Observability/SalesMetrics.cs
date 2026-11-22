using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TicketRush.Application.Abstractions.Observability;

namespace TicketRush.Infrastructure.Observability;

/// <summary>
/// Meter <c>TicketRush</c> (spec 17). The gauges report the last values the worker observed, so a scrape never touches Redis
/// or the database and an outage cannot make a metrics callback throw.
/// </summary>
public sealed class SalesMetrics : ISalesMetrics
{
    public const string MeterName = "TicketRush";

    private readonly Counter<long> _holds;
    private readonly Counter<long> _holdsExpired;
    private readonly Counter<long> _admitted;
    private readonly Counter<long> _paid;
    private readonly Counter<long> _refunds;
    private readonly Histogram<double> _gateway;
    private readonly Counter<long> _drift;
    private readonly ConcurrentDictionary<Guid, (long Length, long Active)> _queues = new();
    private (double Lag, long Dead)? _outbox;

    public SalesMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _holds = meter.CreateCounter<long>("ticketrush.holds.created_total", description: "Hold attempts, by result (ok or the error code).");
        _holdsExpired = meter.CreateCounter<long>("ticketrush.holds.expired_total", description: "Holds released by the expiry sweep.");
        _admitted = meter.CreateCounter<long>("ticketrush.queue.admitted_total", description: "Buyers let out of the waiting room.");
        _paid = meter.CreateCounter<long>("ticketrush.orders.paid_total", description: "Orders paid.");
        _refunds = meter.CreateCounter<long>("ticketrush.orders.refund_total", description: "Order refunds requested, by reason.");
        _gateway = meter.CreateHistogram<double>(
            "ticketrush.payment.gateway.duration",
            unit: "s",
            description: "Payment gateway calls, retries included, by operation and outcome.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [0.025, 0.05, 0.1, 0.25, 0.5, 1, 2, 3, 5, 10] });
        _drift = meter.CreateCounter<long>("ticketrush.inventory.drift_fixed_total", description: "Corrections the reconciler made to Redis inventory.");

        meter.CreateObservableGauge("ticketrush.queue.length", () => Queues(q => q.Length), description: "Buyers waiting, per open sale.");
        meter.CreateObservableGauge("ticketrush.queue.active_shoppers", () => Queues(q => q.Active), description: "Buyers admitted and shopping, per open sale.");
        meter.CreateObservableGauge("ticketrush.outbox.lag_seconds", () => Outbox(o => o.Lag), unit: "s", description: "Age of the oldest due outbox message.");
        meter.CreateObservableGauge("ticketrush.outbox.dead_letters", () => Outbox(o => (double)o.Dead), description: "Dead letters waiting for an admin.");
    }

    public void HoldCreated(string result) => _holds.Add(1, new KeyValuePair<string, object?>("result", result));

    public void HoldsExpired(int count)
    {
        if (count > 0)
        {
            _holdsExpired.Add(count);
        }
    }

    public void Admitted(int count)
    {
        if (count > 0)
        {
            _admitted.Add(count);
        }
    }

    public void QueueObserved(Guid phaseId, long length, long activeShoppers) => _queues[phaseId] = (Math.Max(0, length), Math.Max(0, activeShoppers));

    public void QueueClosed(Guid phaseId) => _queues.TryRemove(phaseId, out _);

    public void OrderPaid() => _paid.Add(1);

    public void RefundRequested(string reason) => _refunds.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void GatewayCall(string operation, TimeSpan duration, bool succeeded) =>
        _gateway.Record(
            duration.TotalSeconds,
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error"));

    public void OutboxObserved(double lagSeconds, long deadLetters) => _outbox = (Math.Max(0, lagSeconds), deadLetters);

    public void DriftFixed(int corrections)
    {
        if (corrections > 0)
        {
            _drift.Add(corrections);
        }
    }

    private IEnumerable<Measurement<long>> Queues(Func<(long Length, long Active), long> pick) =>
        _queues.Select(q => new Measurement<long>(pick(q.Value), new KeyValuePair<string, object?>("phase", q.Key.ToString())));

    private IEnumerable<Measurement<double>> Outbox(Func<(double Lag, long Dead), double> pick) =>
        _outbox is { } o ? [new Measurement<double>(pick(o))] : [];
}
