using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

/// <summary>Which refund: the whole order's (order-level <c>RefundRequested</c>) or one payment session's (<c>PaymentRefundRequested</c>).</summary>
public enum RefundTarget
{
    Order,
    Session,
}

/// <param name="Target">Order or session.</param>
/// <param name="Id">The order id or the payment attempt id.</param>
public sealed record RefundWorkItem(RefundTarget Target, Guid Id);

/// <summary>What one refund try did.</summary>
public enum RefundOutcome
{
    /// <summary>The gateway confirmed it: the order (or session) is Refunded.</summary>
    Refunded,

    /// <summary>The gateway declined it: counted and retried after a backoff.</summary>
    Declined,

    /// <summary>Declined too often, or impossible to even ask: parked for a person (still RefundPending, never dropped).</summary>
    Parked,

    /// <summary>The gateway could not be reached: tried again on a later pass, nothing counted.</summary>
    SkippedOutage,

    /// <summary>Nothing to do (already refunded by another node, not due yet, parked).</summary>
    Unchanged,
}

/// <summary>
/// Spec 08 RefundWorker, one refund at a time. Driven by database state (rows in <c>RefundPending</c>), not by the outbox,
/// so a refund the gateway keeps declining is never lost to a dead letter. Exactly once at the gateway comes from the
/// idempotency key: <see cref="OrderKey"/> for an order, <see cref="SessionKey"/> for one session. A crash after the
/// gateway refunded but before the commit, or two workers at once, simply re-issue the same key and get the same refund;
/// the <c>xmin</c> check lets only one of them commit Refunded (and its <c>OrderRefunded</c> / <c>PaymentRefunded</c>).
/// <para>
/// Amounts: an order refunds what its one <see cref="PaymentAttemptStatus.Succeeded"/> session collected
/// (<see cref="PaymentAttempt.Amount"/>, the money the fulfil path accepted); a session refunds the amount its refund
/// request recorded (<see cref="PaymentAttempt.RefundAmount"/>). Missing data parks the refund instead of guessing.
/// </para>
/// </summary>
public sealed partial class ProcessRefundHandler(
    IAppDbContext db,
    IPaymentGatewayResolver gateways,
    IOptions<RefundOptions> options,
    TimeProvider time,
    ILogger<ProcessRefundHandler> logger)
{
    public const int MaxConcurrencyRetries = 3;

    private sealed record GatewayRefund(string Provider, string SessionId, Money Amount, string IdempotencyKey);

    /// <summary>Gateway idempotency key of an order-level refund (spec 08: the order id).</summary>
    public static string OrderKey(Guid orderId) => orderId.ToString();

    /// <summary>Gateway idempotency key of a session-level refund; never collides with <see cref="OrderKey"/>.</summary>
    public static string SessionKey(Guid attemptId) => $"attempt:{attemptId}";

    /// <summary>Refunds that are due now (not parked, backoff over), oldest (v7 id) first, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<RefundWorkItem>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.Status == OrderStatus.RefundPending && !o.Refund.NeedsAttention && (o.Refund.NextAttemptAt == null || o.Refund.NextAttemptAt <= now))
            .OrderBy(o => o.Id).Take(limit).Select(o => o.Id).ToListAsync(ct);
        var sessions = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.Status == PaymentAttemptStatus.RefundPending && !a.Refund.NeedsAttention && (a.Refund.NextAttemptAt == null || a.Refund.NextAttemptAt <= now))
            .OrderBy(a => a.Id).Take(limit).Select(a => a.Id).ToListAsync(ct);
        return orders.Select(id => new RefundWorkItem(RefundTarget.Order, id))
            .Concat(sessions.Select(id => new RefundWorkItem(RefundTarget.Session, id)))
            .OrderBy(item => item.Id).Take(limit).ToList();
    }

    /// <summary>Refunds parked for a person (orders and sessions), for the alerting gauge.</summary>
    public async Task<int> CountParkedAsync(CancellationToken ct) =>
        await db.Orders.CountAsync(o => o.Status == OrderStatus.RefundPending && o.Refund.NeedsAttention, ct)
        + await db.PaymentAttempts.CountAsync(a => a.Status == PaymentAttemptStatus.RefundPending && a.Refund.NeedsAttention, ct);

    /// <param name="unavailableProviders">Providers that failed during this pass; a provider that fails here is added.</param>
    public async Task<RefundOutcome> HandleAsync(RefundWorkItem item, ISet<string> unavailableProviders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(unavailableProviders);
        for (var retry = 0; ; retry++)
        {
            try
            {
                return item.Target == RefundTarget.Order
                    ? await RefundOrderAsync(item.Id, unavailableProviders, ct)
                    : await RefundSessionAsync(item.Id, unavailableProviders, ct);
            }
            catch (DbUpdateConcurrencyException ex) when (retry < MaxConcurrencyRetries)
            {
                // Another node committed this refund (or B9 changed the order) after we read it: read again, decide again.
                LogRetry(item.Target, item.Id, retry + 1, ex);
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>
    /// A try failed with something unexpected (an adapter bug, a bad response): record it like a decline, so the refund
    /// backs off, counts toward parking, and cannot hold the head of every batch. Only the exception TYPE is stored
    /// (messages may carry provider data); the caller uses a fresh scope.
    /// </summary>
    public async Task<RefundOutcome> RecordFailureAsync(RefundWorkItem item, string exceptionType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        IRefundable? target = item.Target == RefundTarget.Order
            ? await db.Orders.SingleOrDefaultAsync(o => o.Id == item.Id && o.Status == OrderStatus.RefundPending, ct)
            : await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Id == item.Id && a.Status == PaymentAttemptStatus.RefundPending, ct);
        return target is null
            ? RefundOutcome.Unchanged
            : await DeclineAsync(item.Target, target, $"error: {exceptionType}", time.GetUtcNow(), ct);
    }

    private async Task<RefundOutcome> RefundOrderAsync(Guid orderId, ISet<string> unavailable, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is not { Status: OrderStatus.RefundPending } || !order.Refund.IsDue(now))
        {
            return RefundOutcome.Unchanged;
        }

        // The fulfil path keeps one Succeeded session per order: the one whose money this refund sends back.
        var paid = await db.PaymentAttempts.Where(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.Succeeded).Take(2).ToListAsync(ct);
        if (paid.Count != 1)
        {
            return await ParkAsync(RefundTarget.Order, order, paid.Count == 0 ? "no_succeeded_session" : "several_succeeded_sessions", ct);
        }

        var session = paid[0];
        var refund = new GatewayRefund(session.Provider, session.ProviderSessionId, session.Amount, OrderKey(order.Id));
        return await RefundAsync(RefundTarget.Order, order, refund, unavailable, now, ct);
    }

    private async Task<RefundOutcome> RefundSessionAsync(Guid attemptId, ISet<string> unavailable, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var attempt = await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is not { Status: PaymentAttemptStatus.RefundPending } || !attempt.Refund.IsDue(now))
        {
            return RefundOutcome.Unchanged;
        }

        if (attempt.RefundAmount is not { } amount)
        {
            return await ParkAsync(RefundTarget.Session, attempt, "missing_refund_amount", ct);
        }

        var refund = new GatewayRefund(attempt.Provider, attempt.ProviderSessionId, amount, SessionKey(attempt.Id));
        return await RefundAsync(RefundTarget.Session, attempt, refund, unavailable, now, ct);
    }

    private async Task<RefundOutcome> RefundAsync(
        RefundTarget kind, IRefundable target, GatewayRefund refund, ISet<string> unavailable, DateTimeOffset now, CancellationToken ct)
    {
        if (gateways.Get(refund.Provider) is not { } gateway)
        {
            return await ParkAsync(kind, target, "unknown_provider", ct);
        }

        if (unavailable.Contains(gateway.Provider))
        {
            return RefundOutcome.SkippedOutage;
        }

        var result = await CallGatewayAsync(kind, target, gateway, refund, ct);
        if (result is null)
        {
            unavailable.Add(gateway.Provider);
            return RefundOutcome.SkippedOutage;
        }

        return result.IsSuccess
            ? await CompleteAsync(kind, target, result, refund.Amount, ct)
            : await DeclineAsync(kind, target, result.FailureReason ?? "declined", now, ct);
    }

    /// <returns>The gateway's answer, or <c>null</c> when it could not be reached.</returns>
    private async Task<RefundResult?> CallGatewayAsync(RefundTarget kind, IRefundable target, IPaymentGateway gateway, GatewayRefund refund, CancellationToken ct)
    {
        try
        {
            return await gateway.RefundAsync(refund.SessionId, refund.Amount, refund.IdempotencyKey, ct);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            LogOutage(kind, target.Id, gateway.Provider, ex);
            return null;
        }
        catch (PaymentRejectedException ex)
        {
            // A refusal thrown instead of returned is still a decline: counted, backed off, eventually parked.
            return new RefundResult(string.Empty, RefundStatus.Failed, $"rejected: {ex.Message}");
        }
    }

    private async Task<RefundOutcome> CompleteAsync(RefundTarget kind, IRefundable target, RefundResult result, Money amount, CancellationToken ct)
    {
        target.MarkRefunded();
        await db.SaveChangesAsync(ct);
        LogRefunded(kind, target.Id, amount.Amount, amount.Currency, result.RefundId);
        return RefundOutcome.Refunded;
    }

    private async Task<RefundOutcome> DeclineAsync(RefundTarget kind, IRefundable target, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var declines = target.Refund.Attempts + 1;
        var park = declines >= options.Value.MaxDeclines;
        target.RecordRefundDecline(reason, park ? null : now + options.Value.DelayAfter(declines));
        await db.SaveChangesAsync(ct);
        if (park)
        {
            LogParked(kind, target.Id, reason, declines);
            return RefundOutcome.Parked;
        }

        LogDeclined(kind, target.Id, reason, declines, target.Refund.NextAttemptAt);
        return RefundOutcome.Declined;
    }

    private async Task<RefundOutcome> ParkAsync(RefundTarget kind, IRefundable target, string reason, CancellationToken ct)
    {
        target.ParkRefund(reason);
        await db.SaveChangesAsync(ct);
        LogParked(kind, target.Id, reason, target.Refund.Attempts);
        return RefundOutcome.Parked;
    }

    [LoggerMessage(EventId = 8211, Level = LogLevel.Information, Message = "Refund of {Target} {Id} confirmed: {Amount} {Currency}, gateway refund {RefundId}.")]
    private partial void LogRefunded(RefundTarget target, Guid id, long amount, string currency, string refundId);

    [LoggerMessage(EventId = 8212, Level = LogLevel.Warning, Message = "Refund of {Target} {Id} declined ({Reason}), decline {Declines}; next try at {NextAttemptAt}.")]
    private partial void LogDeclined(RefundTarget target, Guid id, string reason, int declines, DateTimeOffset? nextAttemptAt);

    [LoggerMessage(EventId = 8213, Level = LogLevel.Error, Message = "Refund of {Target} {Id} NEEDS ATTENTION ({Reason}) after {Declines} declines; parked until a person resumes it.")]
    private partial void LogParked(RefundTarget target, Guid id, string reason, int declines);

    [LoggerMessage(EventId = 8214, Level = LogLevel.Warning, Message = "Refund of {Target} {Id} not tried: {Provider} is unavailable; retried on a later pass.")]
    private partial void LogOutage(RefundTarget target, Guid id, string provider, Exception exception);

    [LoggerMessage(EventId = 8215, Level = LogLevel.Information, Message = "Refund of {Target} {Id} collided with a concurrent change (retry {Retry}).")]
    private partial void LogRetry(RefundTarget target, Guid id, int retry, Exception exception);
}
