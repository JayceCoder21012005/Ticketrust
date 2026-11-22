using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Orders;

namespace TicketRush.Application.Features.Orders;

/// <summary>What the expiry sweep did with one order, or (session reconcile) with one payment session.</summary>
public enum OrderExpiryOutcome
{
    /// <summary>No session holds money: the order is Expired (its sessions are reconciled on their own).</summary>
    Expired,

    /// <summary>A session had succeeded after all: the order is Paid through the webhook path.</summary>
    FulfilledLate,

    /// <summary>A session had succeeded but the money goes back (seats gone, event cancelled, duplicate payment, closed order).</summary>
    RefundLate,

    /// <summary>The gateway could not be asked: nothing is changed until a later pass.</summary>
    SkippedOutage,

    /// <summary>Nothing to do (no longer open, not yet due, or the fulfil path had already decided on it).</summary>
    Unchanged,

    /// <summary>Session reconcile: the gateway says the session failed or expired, so it is closed (Failed / Expired).</summary>
    Closed,

    /// <summary>Session reconcile: the gateway still reports Pending; asked again on a later pass.</summary>
    StillPending,
}

/// <summary>
/// Where the next page starts: after <see cref="ExpiresAt"/>, or at it for the ids not seen yet (ties).
/// </summary>
public sealed record ExpiryCursor(DateTimeOffset ExpiresAt, IReadOnlyList<Guid> SeenAtExpiresAt);

/// <param name="OrderIds">Due ids (orders, or payment attempts for the session reconcile), oldest expiry first.</param>
/// <param name="Next">Cursor for the following page.</param>
public sealed record DueOrders(IReadOnlyList<Guid> OrderIds, ExpiryCursor? Next);

/// <summary>
/// Spec 08 OrderExpiryService, one order (or one session) at a time.
/// <para>
/// <b>Orders.</b> An open order (Pending, AwaitingPayment, PaymentFailed) past <see cref="Order.ExpiresAt"/> is only
/// expired once the gateway confirmed that none of its sessions took money:
/// <list type="bullet">
/// <item>any session reports <see cref="PaymentStatus.Succeeded"/> → the webhook decision table runs for it
/// (<see cref="FulfillOrderHandler"/> with the synthetic provider event id <c>status:{sessionId}</c>);</item>
/// <item>the gateway cannot be reached → the order stays open until a later pass (a payment may exist);</item>
/// <item>otherwise → <see cref="Order.Expire"/> alone, one <c>SaveChanges</c> guarded by the order's <c>xmin</c> (a webhook
/// that committed in between wins: the conflict re-reads and decides again). Its sessions stay Pending.</item>
/// </list>
/// </para>
/// <para>
/// <b>Sessions.</b> A session still Pending more than <see cref="OrderExpiryOptions.AttemptGrace"/> after its own expiry,
/// whose order is no longer open (Expired, Paid, Cancelled, RefundPending, Refunded), is asked again: Succeeded → the same
/// fulfil path (late fulfilment, or a duplicate-payment / closed-order refund of that session); Failed / Expired → closed.
/// This finds money the order sweep could not: a payment completed just after the sweep's Pending read whose webhooks were
/// exhausted, a second succeeded session whose fulfilment failed after the first committed, sessions of an order another
/// session paid or that something else (B9) closed.
/// </para>
/// <para>
/// <b>Limitation:</b> status reads carry no captured amount. The fulfil path is given the session's own amount, so a gateway
/// that captured less than asked is not caught here; the provider's real webhook then reports the difference and
/// <see cref="FulfillOrderHandler"/> flags it (<see cref="PaymentWebhookOutcome.AmountMismatchAfterStatus"/>, Error log)
/// for a person instead of ignoring it.
/// </para>
/// The Redis hold needs no release here: its TTL is the order's <see cref="Order.ExpiresAt"/>, so it is already gone.
/// </summary>
public sealed partial class ExpireOrderHandler(
    IAppDbContext db,
    IPaymentGatewayResolver gateways,
    FulfillOrderHandler fulfil,
    Resale.ResaleSettlement resale,
    IOptions<OrderExpiryOptions> options,
    TimeProvider time,
    ILogger<ExpireOrderHandler> logger)
{
    public const int MaxConcurrencyRetries = 3;

    /// <summary>The provider event id the sweep's own decision is recorded under in <c>processed_webhooks</c>.</summary>
    public static string StatusEventId(string sessionId) => FulfillOrderHandler.StatusEventId(sessionId);

    /// <summary>
    /// Open orders whose expiry is at or before <paramref name="now"/>, by expiry then id (index <c>ix_orders_open_expires_at</c>).
    /// </summary>
    public async Task<DueOrders> ListDueAsync(DateTimeOffset now, ExpiryCursor? after, int limit, CancellationToken ct)
    {
        // Literal statuses (not a parameter list) so the predicate matches the partial index's filter.
        var due = db.Orders.AsNoTracking().Where(o =>
            (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed)
            && o.ExpiresAt <= now);
        if (after is not null)
        {
            due = due.Where(o => o.ExpiresAt > after.ExpiresAt || (o.ExpiresAt == after.ExpiresAt && !after.SeenAtExpiresAt.Contains(o.Id)));
        }

        var page = await due.OrderBy(o => o.ExpiresAt).ThenBy(o => o.Id).Take(limit).Select(o => new { o.Id, o.ExpiresAt }).ToListAsync(ct);
        return Page([.. page.Select(o => (o.Id, o.ExpiresAt))], after);
    }

    /// <summary>
    /// Pending sessions past their expiry plus the grace whose order is no longer open, by expiry then id
    /// (index <c>ix_payment_attempts_pending_expires_at</c>).
    /// </summary>
    public async Task<DueOrders> ListStaleSessionsAsync(DateTimeOffset now, ExpiryCursor? after, int limit, CancellationToken ct)
    {
        var cutoff = now - options.Value.AttemptGrace;
        var due = db.PaymentAttempts.AsNoTracking().Where(a => a.Status == PaymentAttemptStatus.Pending && a.ExpiresAt <= cutoff
            && db.Orders.Any(o => o.Id == a.OrderId
                && o.Status != OrderStatus.Pending && o.Status != OrderStatus.AwaitingPayment && o.Status != OrderStatus.PaymentFailed));
        if (after is not null)
        {
            due = due.Where(a => a.ExpiresAt > after.ExpiresAt || (a.ExpiresAt == after.ExpiresAt && !after.SeenAtExpiresAt.Contains(a.Id)));
        }

        var page = await due.OrderBy(a => a.ExpiresAt).ThenBy(a => a.Id).Take(limit).Select(a => new { a.Id, a.ExpiresAt }).ToListAsync(ct);
        return Page([.. page.Select(a => (a.Id, a.ExpiresAt))], after);
    }

    private static DueOrders Page(IReadOnlyList<(Guid Id, DateTimeOffset At)> page, ExpiryCursor? after)
    {
        if (page.Count == 0)
        {
            return new DueOrders([], after);
        }

        var last = page[^1].At;
        var seen = page.Where(p => p.At == last).Select(p => p.Id);
        var carried = after is not null && after.ExpiresAt == last ? after.SeenAtExpiresAt : [];
        return new DueOrders([.. page.Select(p => p.Id)], new ExpiryCursor(last, [.. carried, .. seen]));
    }

    /// <param name="unavailableProviders">
    /// Providers that failed during this pass; their orders are skipped without another call, and a provider that fails here is added.
    /// </param>
    public Task<OrderExpiryOutcome> HandleAsync(Guid orderId, ISet<string> unavailableProviders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unavailableProviders);
        return RetryOnConflictAsync(orderId, () => DecideAsync(orderId, unavailableProviders, ct));
    }

    /// <summary>Asks the gateway about one stale Pending session of a closed order (see the class remarks).</summary>
    public Task<OrderExpiryOutcome> ReconcileSessionAsync(Guid attemptId, ISet<string> unavailableProviders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unavailableProviders);
        return RetryOnConflictAsync(attemptId, () => ReconcileAsync(attemptId, unavailableProviders, ct));
    }

    private async Task<OrderExpiryOutcome> RetryOnConflictAsync(Guid id, Func<Task<OrderExpiryOutcome>> decide)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await decide();
            }
            catch (DbUpdateConcurrencyException ex) when (retry < MaxConcurrencyRetries)
            {
                // A webhook (or a second sweeper) changed the row after we read it: decide again from fresh reads.
                LogRetry(id, retry + 1, ex);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<OrderExpiryOutcome> DecideAsync(Guid orderId, ISet<string> unavailable, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var order = await db.Orders.SingleOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || !order.IsOpen || order.ExpiresAt > now)
        {
            return OrderExpiryOutcome.Unchanged;
        }

        var attempts = await db.PaymentAttempts.AsNoTracking().Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var succeeded = await SucceededSessionsAsync(attempts.Where(MayHoldMoney).ToList(), unavailable, ct);
        if (succeeded is null)
        {
            return OrderExpiryOutcome.SkippedOutage;
        }

        return succeeded.Count > 0 ? await FulfilLateAsync(orderId, succeeded, now, ct) : await ExpireAsync(order, ct);
    }

    private async Task<OrderExpiryOutcome> ReconcileAsync(Guid attemptId, ISet<string> unavailable, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var attempt = await db.PaymentAttempts.SingleOrDefaultAsync(a => a.Id == attemptId, ct);
        if (attempt is not { Status: PaymentAttemptStatus.Pending } || attempt.ExpiresAt > now - options.Value.AttemptGrace
            || await db.Orders.AnyAsync(o => o.Id == attempt.OrderId
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed), ct))
        {
            // Open orders are the order sweep's: it asks every session before it decides.
            return OrderExpiryOutcome.Unchanged;
        }

        return await StatusOfAsync(attempt, unavailable, ct) switch
        {
            null => OrderExpiryOutcome.SkippedOutage,
            PaymentStatus.Succeeded => await FulfilLateAsync(attempt.OrderId, [attempt], now, ct),
            PaymentStatus.Failed => await CloseAsync(attempt, failed: true, ct),
            PaymentStatus.Expired => await CloseAsync(attempt, failed: false, ct),
            _ => OrderExpiryOutcome.StillPending,
        };
    }

    /// <summary>
    /// Sessions whose money is not decided yet. Failed and Expired are asked too: the decision table accepts a success after
    /// either (out-of-order providers), so the sweep must not expire an order whose "failed" session in fact took money.
    /// </summary>
    private static bool MayHoldMoney(PaymentAttempt attempt) =>
        attempt.Status is PaymentAttemptStatus.Pending or PaymentAttemptStatus.Failed or PaymentAttemptStatus.Expired;

    /// <returns>The sessions that report success, or <c>null</c> when a session could not be read (outage).</returns>
    private async Task<List<PaymentAttempt>?> SucceededSessionsAsync(IReadOnlyList<PaymentAttempt> attempts, ISet<string> unavailable, CancellationToken ct)
    {
        var succeeded = new List<PaymentAttempt>();
        foreach (var attempt in attempts)
        {
            var status = await StatusOfAsync(attempt, unavailable, ct);
            if (status is null)
            {
                return null;
            }

            if (status == PaymentStatus.Succeeded)
            {
                succeeded.Add(attempt);
            }
        }

        return succeeded;
    }

    private async Task<PaymentStatus?> StatusOfAsync(PaymentAttempt attempt, ISet<string> unavailable, CancellationToken ct)
    {
        // An attempt of a provider nobody registered is a deployment error: it must be loud, and nothing may be decided.
        var gateway = gateways.Get(attempt.Provider)
            ?? throw new InvalidOperationException($"No payment gateway is registered for provider '{attempt.Provider}'.");
        if (unavailable.Contains(gateway.Provider))
        {
            return null;
        }

        try
        {
            return await gateway.GetStatusAsync(attempt.ProviderSessionId, ct);
        }
        catch (PaymentGatewayUnavailableException ex)
        {
            unavailable.Add(gateway.Provider);
            LogOutage(attempt.OrderId, attempt.Id, gateway.Provider, ex);
            return null;
        }
    }

    /// <summary>
    /// The same decision a <c>Succeeded</c> webhook gets, per succeeded session in creation order: the first pays the order
    /// (or sends it to refund), any further one is refunded as a duplicate payment.
    /// </summary>
    private async Task<OrderExpiryOutcome> FulfilLateAsync(Guid orderId, IReadOnlyList<PaymentAttempt> succeeded, DateTimeOffset now, CancellationToken ct)
    {
        var sessions = succeeded.Select(a => (a.Id, a.Provider, a.ProviderSessionId, a.Amount)).ToList();
        // The fulfil path reads everything itself (and clears the tracker when it retries): hand it nothing tracked.
        db.ChangeTracker.Clear();
        var outcomes = new List<PaymentWebhookOutcome>();
        foreach (var (attemptId, provider, sessionId, amount) in sessions)
        {
            var webhook = new WebhookEvent(StatusEventId(sessionId), sessionId, WebhookEventType.Succeeded, amount, now);
            var outcome = await fulfil.HandleAsync(provider, webhook, ct);
            LogLateSuccess(orderId, attemptId, outcome);
            outcomes.Add(outcome);
        }

        return outcomes.Contains(PaymentWebhookOutcome.Paid) ? OrderExpiryOutcome.FulfilledLate
            : outcomes.Contains(PaymentWebhookOutcome.RefundPending) ? OrderExpiryOutcome.RefundLate
            : OrderExpiryOutcome.Unchanged;
    }

    private async Task<OrderExpiryOutcome> ExpireAsync(Order order, CancellationToken ct)
    {
        // Only the order: a session left Pending is asked again by the session reconcile once the grace is over, so money
        // that reaches it after this read is still found.
        order.Expire();
        if (order.Kind == OrderKind.Resale)
        {
            // Spec 14: the reservation ends with the order; the listing is back on sale.
            await resale.ReleaseAsync(order, ct);
        }

        await db.SaveChangesAsync(ct);
        LogExpired(order.Id);
        return OrderExpiryOutcome.Expired;
    }

    private async Task<OrderExpiryOutcome> CloseAsync(PaymentAttempt attempt, bool failed, CancellationToken ct)
    {
        if (failed)
        {
            attempt.MarkFailed();
        }
        else
        {
            attempt.MarkExpired();
        }

        await db.SaveChangesAsync(ct);
        LogSessionClosed(attempt.OrderId, attempt.Id, attempt.Status);
        return OrderExpiryOutcome.Closed;
    }

    [LoggerMessage(EventId = 8201, Level = LogLevel.Information, Message = "Order {OrderId} expired; its pending sessions are reconciled after the grace.")]
    private partial void LogExpired(Guid orderId);

    [LoggerMessage(EventId = 8202, Level = LogLevel.Warning, Message = "Order {OrderId}: session {AttemptId} had succeeded; decided through the fulfil path: {Outcome}.")]
    private partial void LogLateSuccess(Guid orderId, Guid attemptId, PaymentWebhookOutcome outcome);

    [LoggerMessage(EventId = 8203, Level = LogLevel.Warning, Message = "Order {OrderId}: the status of session {AttemptId} could not be read from {Provider}; nothing changed, retried on a later pass.")]
    private partial void LogOutage(Guid orderId, Guid attemptId, string provider, Exception exception);

    [LoggerMessage(EventId = 8204, Level = LogLevel.Information, Message = "Expiry decision for {Id} collided with a concurrent change (retry {Retry}).")]
    private partial void LogRetry(Guid id, int retry, Exception exception);

    [LoggerMessage(EventId = 8206, Level = LogLevel.Information, Message = "Order {OrderId}: stale session {AttemptId} closed as {Status}.")]
    private partial void LogSessionClosed(Guid orderId, Guid attemptId, PaymentAttemptStatus status);
}
