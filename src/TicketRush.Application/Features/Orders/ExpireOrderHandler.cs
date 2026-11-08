using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Orders;

namespace TicketRush.Application.Features.Orders;

/// <summary>What the expiry sweep did with one order.</summary>
public enum OrderExpiryOutcome
{
    /// <summary>No session holds money: the order is Expired and its pending sessions too.</summary>
    Expired,

    /// <summary>A session had succeeded after all (late success, seats free): the order is Paid through the webhook path.</summary>
    FulfilledLate,

    /// <summary>A session had succeeded but the order cannot be fulfilled (seats gone, event cancelled): the money goes back.</summary>
    RefundLate,

    /// <summary>The gateway could not be asked: a payment may exist, so the order is left open until a later pass.</summary>
    SkippedOutage,

    /// <summary>Nothing to do (no longer open, not yet expired, or the fulfil path had already decided on it).</summary>
    Unchanged,
}

/// <summary>
/// Where the next page of due orders starts: after <see cref="ExpiresAt"/>, or at it for the ids not seen yet (ties).
/// </summary>
public sealed record ExpiryCursor(DateTimeOffset ExpiresAt, IReadOnlyList<Guid> SeenAtExpiresAt);

/// <param name="OrderIds">Due open orders, oldest expiry first.</param>
/// <param name="Next">Cursor for the following page.</param>
public sealed record DueOrders(IReadOnlyList<Guid> OrderIds, ExpiryCursor? Next);

/// <summary>
/// Spec 08 OrderExpiryService, one order at a time. An open order (Pending, AwaitingPayment, PaymentFailed) past its
/// <see cref="Order.ExpiresAt"/> is only expired once the gateway confirmed that none of its sessions took money:
/// <list type="bullet">
/// <item>any session reports <see cref="PaymentStatus.Succeeded"/> → the webhook decision table runs for it
/// (<see cref="FulfillOrderHandler"/> with the synthetic provider event id <c>status:{sessionId}</c>): Paid when the seats are
/// still free (C3 late success), RefundPending when they are gone. The order is not expired;</item>
/// <item>the gateway cannot be reached → the order stays open until a later pass (a payment may exist);</item>
/// <item>otherwise (Pending, Failed, Expired, or no session at all) → <see cref="Order.Expire"/> and every pending session
/// <see cref="PaymentAttempt.MarkExpired"/>, in one <c>SaveChanges</c> (one transaction) guarded by the rows' <c>xmin</c>.
/// A webhook that committed in between wins: the conflict re-reads and decides again.</item>
/// </list>
/// The Redis hold needs no release here: its TTL is the order's <see cref="Order.ExpiresAt"/>, so it is already gone
/// (and the hold sweeper frees its seats).
/// </summary>
public sealed partial class ExpireOrderHandler(
    IAppDbContext db,
    IPaymentGatewayResolver gateways,
    FulfillOrderHandler fulfil,
    TimeProvider time,
    ILogger<ExpireOrderHandler> logger)
{
    public const int MaxConcurrencyRetries = 3;

    /// <summary>The provider event id the sweep's own decision is recorded under in <c>processed_webhooks</c>.</summary>
    public static string StatusEventId(string sessionId) => $"status:{sessionId}";

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
        if (page.Count == 0)
        {
            return new DueOrders([], after);
        }

        var last = page[^1].ExpiresAt;
        var seen = page.Where(o => o.ExpiresAt == last).Select(o => o.Id);
        var carried = after is not null && after.ExpiresAt == last ? after.SeenAtExpiresAt : [];
        return new DueOrders([.. page.Select(o => o.Id)], new ExpiryCursor(last, [.. carried, .. seen]));
    }

    /// <param name="unavailableProviders">
    /// Providers that failed during this pass; their orders are skipped without another call, and a provider that fails here is added.
    /// </param>
    public async Task<OrderExpiryOutcome> HandleAsync(Guid orderId, ISet<string> unavailableProviders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(unavailableProviders);
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await DecideAsync(orderId, unavailableProviders, ct);
            }
            catch (DbUpdateConcurrencyException ex) when (retry < MaxConcurrencyRetries)
            {
                // A webhook (or a second sweeper) changed the order or one of its sessions after we read them: decide again.
                LogRetry(orderId, retry + 1, ex);
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

        var attempts = await db.PaymentAttempts.Where(a => a.OrderId == orderId).OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var succeeded = await SucceededSessionsAsync(attempts.Where(MayHoldMoney).ToList(), unavailable, ct);
        if (succeeded is null)
        {
            return OrderExpiryOutcome.SkippedOutage;
        }

        return succeeded.Count > 0 ? await FulfilLateAsync(orderId, succeeded, now, ct) : await ExpireAsync(order, attempts, ct);
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
        // An attempt of a provider nobody registered is a deployment error: it must be loud, and the order must not expire.
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

    private async Task<OrderExpiryOutcome> ExpireAsync(Order order, IReadOnlyList<PaymentAttempt> attempts, CancellationToken ct)
    {
        order.Expire();
        var pending = attempts.Where(a => a.Status == PaymentAttemptStatus.Pending).ToList();
        foreach (var attempt in pending)
        {
            attempt.MarkExpired();
        }

        // One SaveChanges is one transaction; every changed row is checked against the xmin we read (a webhook may have won).
        await db.SaveChangesAsync(ct);
        LogExpired(order.Id, order.Status, pending.Count);
        return OrderExpiryOutcome.Expired;
    }

    [LoggerMessage(EventId = 8201, Level = LogLevel.Information, Message = "Order {OrderId} expired ({Status}); {Sessions} pending payment sessions expired with it.")]
    private partial void LogExpired(Guid orderId, OrderStatus status, int sessions);

    [LoggerMessage(EventId = 8202, Level = LogLevel.Warning, Message = "Order {OrderId} was past its expiry but session {AttemptId} had succeeded; decided through the fulfil path: {Outcome}.")]
    private partial void LogLateSuccess(Guid orderId, Guid attemptId, PaymentWebhookOutcome outcome);

    [LoggerMessage(EventId = 8203, Level = LogLevel.Warning, Message = "Order {OrderId} not expired: the status of session {AttemptId} could not be read from {Provider}; retried on a later pass.")]
    private partial void LogOutage(Guid orderId, Guid attemptId, string provider, Exception exception);

    [LoggerMessage(EventId = 8204, Level = LogLevel.Information, Message = "Expiring order {OrderId} collided with a concurrent change (retry {Retry}).")]
    private partial void LogRetry(Guid orderId, int retry, Exception exception);
}
