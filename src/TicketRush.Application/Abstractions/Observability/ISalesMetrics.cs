namespace TicketRush.Application.Abstractions.Observability;

/// <summary>
/// The flash-sale metrics of spec 17 (meter <c>TicketRush</c>). Recording never throws and never blocks: a metric is
/// best effort and must not fail the request or the sweep that records it.
/// </summary>
public interface ISalesMetrics
{
    /// <summary><c>ticketrush.holds.created_total{result}</c>: <c>ok</c> or the refusal's error code.</summary>
    void HoldCreated(string result);

    /// <summary><c>ticketrush.holds.expired_total</c>: holds the expiry sweep released.</summary>
    void HoldsExpired(int count);

    /// <summary><c>ticketrush.queue.admitted_total</c>.</summary>
    void Admitted(int count);

    /// <summary>The last observed waiting room of a sale, reported by the <c>queue.length</c> and <c>queue.active_shoppers</c> gauges.</summary>
    void QueueObserved(Guid phaseId, long length, long activeShoppers);

    /// <summary>The sale closed: its gauges stop reporting.</summary>
    void QueueClosed(Guid phaseId);

    /// <summary><c>ticketrush.orders.paid_total</c>.</summary>
    void OrderPaid();

    /// <summary><c>ticketrush.orders.refund_total{reason}</c>.</summary>
    void RefundRequested(string reason);

    /// <summary><c>ticketrush.payment.gateway.duration</c> (seconds) by operation and outcome.</summary>
    void GatewayCall(string operation, TimeSpan duration, bool succeeded);

    /// <summary>The last observed outbox: age of the oldest due message and the dead letters waiting for an admin.</summary>
    void OutboxObserved(double lagSeconds, long deadLetters);

    /// <summary><c>ticketrush.inventory.drift_fixed_total</c>: corrections the reconciler made to Redis.</summary>
    void DriftFixed(int corrections);
}
