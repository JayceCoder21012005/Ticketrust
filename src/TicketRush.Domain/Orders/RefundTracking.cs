namespace TicketRush.Domain.Orders;

/// <summary>
/// Retry bookkeeping of a refund the gateway has not confirmed yet (spec 08 RefundWorker). Stored on the refund target
/// itself (the order or the payment session), so a refund the gateway keeps declining is never forgotten: it is retried with
/// backoff and, after too many declines, parked for a person (<see cref="NeedsAttention"/>) instead of being dropped.
/// A gateway outage is not a decline and is not recorded here.
/// </summary>
/// <param name="Attempts">Declines so far (reset when a person resumes the refund).</param>
/// <param name="NextAttemptAt">Earliest next try; <c>null</c> means now.</param>
/// <param name="LastError">The gateway's (or the worker's) last reason, cut to <see cref="MaxErrorLength"/>.</param>
/// <param name="NeedsAttention">Parked: the worker no longer retries it until a person resumes it.</param>
public sealed record RefundTracking(int Attempts, DateTimeOffset? NextAttemptAt, string? LastError, bool NeedsAttention)
{
    /// <summary>Column width of <see cref="LastError"/>.</summary>
    public const int MaxErrorLength = 500;

    public static RefundTracking None { get; } = new(0, null, null, false);

    /// <summary>The worker may try now: not parked and the backoff, if any, is over.</summary>
    public bool IsDue(DateTimeOffset now) => !NeedsAttention && (NextAttemptAt is null || NextAttemptAt <= now);

    /// <param name="retryAt">When to try again; <c>null</c> parks the refund.</param>
    internal RefundTracking Declined(string error, DateTimeOffset? retryAt) =>
        new(Attempts + 1, retryAt, Cut(error), retryAt is null);

    internal RefundTracking Parked(string error) => this with { NextAttemptAt = null, LastError = Cut(error), NeedsAttention = true };

    /// <summary>A person fixed the cause: try again now with a fresh decline count. The last reason stays for the record.</summary>
    internal RefundTracking Resumed() => this with { Attempts = 0, NextAttemptAt = null, NeedsAttention = false };

    private static string Cut(string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return error.Length <= MaxErrorLength ? error : error[..MaxErrorLength];
    }
}

/// <summary>
/// Something the refund worker sends money back for: an order (<c>RefundRequested</c>, gateway key = order id) or a single
/// payment session (<c>PaymentRefundRequested</c>, gateway key <c>attempt:{id}</c>). Every method is legal only while the
/// target waits for its refund.
/// </summary>
public interface IRefundable
{
    Guid Id { get; }

    RefundTracking Refund { get; }

    /// <summary>The gateway confirmed the refund.</summary>
    void MarkRefunded();

    /// <summary>The gateway declined; <paramref name="retryAt"/> <c>null</c> parks the refund for a person.</summary>
    void RecordRefundDecline(string error, DateTimeOffset? retryAt);

    /// <summary>The worker cannot even ask the gateway (missing data, unknown provider): park it for a person.</summary>
    void ParkRefund(string error);

    /// <summary>A person fixed the cause of a parked refund.</summary>
    void ResumeRefund();
}
