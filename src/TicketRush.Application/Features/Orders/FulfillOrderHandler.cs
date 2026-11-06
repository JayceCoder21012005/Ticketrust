using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Orders;

/// <summary>What a verified payment webhook led to. Every value except <see cref="UnknownSession"/> is a committed decision.</summary>
public enum PaymentWebhookOutcome
{
    /// <summary>The order is paid and its tickets are issued.</summary>
    Paid,

    /// <summary>The money goes back: the order is unfulfillable (RefundPending) or only this session's payment is refunded.</summary>
    RefundPending,

    /// <summary>The attempt failed (and the order became PaymentFailed if nothing else can still pay it).</summary>
    Failed,

    /// <summary>This provider event was already decided on (C4).</summary>
    Duplicate,

    /// <summary>Recorded, nothing to change (a Failed after a Succeeded, a refund confirmation, a repeated success).</summary>
    Ignored,

    /// <summary>No payment attempt has this session id. Nothing is written.</summary>
    UnknownSession,
}

/// <summary>
/// Turns a verified payment webhook into exactly one decision (spec 08 "Webhook handling", C1-C4), in ONE transaction:
/// record the provider event in <c>processed_webhooks</c> (a duplicate stops here), load the attempt and its order, then
/// decide. A paid order's inventory is claimed by the <see cref="IInventoryStrategy"/> implementations and the per-user
/// limit is re-checked in the database. When the inventory is gone the transaction is rolled back (dedupe row, tickets and
/// general-admission claims included) and a NEW transaction records the event again and moves the order to RefundPending
/// (C3). Either way the dedupe row commits together with the decision, so a crash in between never swallows an event.
/// A concurrent change of the order or attempt (<c>xmin</c>) or a database deadlock re-runs the whole decision, up to
/// <see cref="MaxConcurrencyRetries"/> times; after that the exception surfaces and nothing is committed.
/// </summary>
public sealed partial class FulfillOrderHandler(
    IAppDbContext db,
    IWebhookInbox inbox,
    IEnumerable<IInventoryStrategy> strategies,
    ITicketPurchaseLimit purchaseLimit,
    IEventSaleLock saleLock,
    TimeProvider time,
    ILogger<FulfillOrderHandler> logger)
{
    public const int MaxConcurrencyRetries = 3;

    private readonly IInventoryStrategy[] _strategies = [.. strategies.OrderBy(s => s.Order)];

    /// <param name="provider">The gateway's canonical <see cref="IPaymentGateway.Provider"/> name.</param>
    /// <param name="webhook">An event whose signature the gateway already verified.</param>
    public async Task<PaymentWebhookOutcome> HandleAsync(string provider, WebhookEvent webhook, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(webhook);
        for (var retry = 0; ; retry++)
        {
            try
            {
                return await DecideAsync(provider, webhook, ct);
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex) && retry < MaxConcurrencyRetries)
            {
                LogRetry(webhook.ProviderEventId, retry + 1, ex);
                db.ChangeTracker.Clear();
            }
        }
    }

    private static bool IsConcurrencyConflict(Exception ex) =>
        ex is DbUpdateConcurrencyException or InventoryConflictException { Reason: InventoryConflictReason.Contention };

    /// <summary>The claiming transaction, or, when the inventory is gone, a fresh transaction that decides without claiming.</summary>
    private async Task<PaymentWebhookOutcome> DecideAsync(string provider, WebhookEvent webhook, CancellationToken ct)
    {
        try
        {
            return await db.InTransactionAsync(token => DecideInTransactionAsync(provider, webhook, unfulfillable: null, token), ct);
        }
        catch (InventoryConflictException ex) when (ex.Reason != InventoryConflictReason.Contention)
        {
            // The rollback removed everything of the first transaction, the dedupe row too: decide again from scratch.
            LogUnfulfillable(webhook.ProviderEventId, ex.Reason);
            db.ChangeTracker.Clear();
            var reason = ex.Reason == InventoryConflictReason.EventCancelled ? RefundReasons.EventCancelled : RefundReasons.SeatsUnavailable;
            return await db.InTransactionAsync(token => DecideInTransactionAsync(provider, webhook, reason, token), ct);
        }
    }

    /// <param name="unfulfillable">Null: claim the inventory. Otherwise the refund reason of an order that cannot be fulfilled.</param>
    private async Task<PaymentWebhookOutcome> DecideInTransactionAsync(string provider, WebhookEvent webhook, string? unfulfillable, CancellationToken ct)
    {
        var attempt = await db.PaymentAttempts.FirstOrDefaultAsync(a => a.Provider == provider && a.ProviderSessionId == webhook.SessionId, ct);
        if (attempt is null)
        {
            // Attempts are saved before the buyer ever sees the gateway page, so this session was never ours. Answering 200
            // stops the provider from retrying forever; nothing is recorded, so a later redelivery is looked at again.
            LogUnknownSession(provider, webhook.ProviderEventId);
            return PaymentWebhookOutcome.UnknownSession;
        }

        var now = time.GetUtcNow();
        if (!await inbox.TryRecordAsync(provider, webhook.ProviderEventId, now, ct))
        {
            return PaymentWebhookOutcome.Duplicate;
        }

        var order = await db.Orders.SingleAsync(o => o.Id == attempt.OrderId, ct);
        var outcome = webhook.Type switch
        {
            WebhookEventType.Succeeded => await SucceededAsync(order, attempt, webhook.Amount, unfulfillable, now, ct),
            WebhookEventType.Failed => await FailedAsync(order, attempt, now, ct),
            // Refund confirmations: the refund worker settles refunds from the gateway's synchronous answer.
            _ => PaymentWebhookOutcome.Ignored,
        };
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private async Task<PaymentWebhookOutcome> SucceededAsync(
        Order order, PaymentAttempt attempt, Money? paid, string? unfulfillable, DateTimeOffset now, CancellationToken ct)
    {
        if (attempt.Status is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.RefundPending or PaymentAttemptStatus.Refunded)
        {
            // This session's money was already decided on (another event id for the same payment): never twice.
            return PaymentWebhookOutcome.Ignored;
        }

        if (paid is not null && paid != attempt.Amount)
        {
            return RefundSession(order, attempt, RefundReasons.AmountMismatch, paid);
        }

        if (order.Status is OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed or OrderStatus.Expired)
        {
            return unfulfillable is null ? await FulfilAsync(order, attempt, now, ct) : MarkUnfulfillable(order, attempt, unfulfillable);
        }

        // Paid, RefundPending, Refunded: another session's money already settled the order. Pending, Cancelled: it takes none.
        var reason = order.Status is OrderStatus.Pending or OrderStatus.Cancelled ? RefundReasons.OrderClosed : RefundReasons.DuplicatePayment;
        return RefundSession(order, attempt, reason, attempt.Amount);
    }

    private async Task<PaymentWebhookOutcome> FulfilAsync(Order order, PaymentAttempt attempt, DateTimeOffset now, CancellationToken ct)
    {
        // Locks are always taken in this order: event row (share), buyer limit, GA tiers, seats.
        await EnsureOnSaleAsync(order.EventId, ct);
        var tickets = order.IssueTickets();
        if (!await purchaseLimit.AllowsAsync(order.EventId, order.UserId, tickets.Count, ct))
        {
            throw new InventoryConflictException(InventoryConflictReason.PurchaseLimitExceeded, "The buyer would exceed the event's ticket limit.");
        }

        foreach (var (strategy, mine) in Partition(tickets))
        {
            await strategy.ClaimAsync(mine, ct);
        }

        order.MarkPaid(now);
        attempt.MarkSucceeded();
        LogPaid(order.Id, attempt.Id, tickets.Count);
        return PaymentWebhookOutcome.Paid;
    }

    /// <summary>
    /// A cancelled (or ended) event issues no tickets, whatever the payment says. The share lock orders a concurrent
    /// cancellation after this transaction, so its Paid → RefundPending sweep cannot miss an order paid here.
    /// </summary>
    private async Task EnsureOnSaleAsync(Guid eventId, CancellationToken ct)
    {
        var status = await saleLock.LockStatusAsync(eventId, ct);
        if (status != EventStatus.Published)
        {
            var reason = status == EventStatus.Cancelled ? InventoryConflictReason.EventCancelled : InventoryConflictReason.EventNotOnSale;
            throw new InventoryConflictException(reason, "The event is no longer on sale.");
        }
    }

    /// <summary>Each ticket goes to exactly one strategy; a ticket no strategy (or two) claims is a wiring bug, never silently skipped.</summary>
    private List<(IInventoryStrategy Strategy, IReadOnlyList<Ticket> Tickets)> Partition(IReadOnlyList<Ticket> tickets)
    {
        if (tickets.Any(ticket => _strategies.Count(s => s.Handles(ticket)) != 1))
        {
            throw new InvalidOperationException("Every ticket must be claimed by exactly one inventory strategy.");
        }

        return _strategies
            .Select(strategy => (strategy, (IReadOnlyList<Ticket>)tickets.Where(strategy.Handles).ToList()))
            .Where(part => part.Item2.Count > 0)
            .ToList();
    }

    /// <summary>
    /// C3: the money arrived but the inventory is gone. This session's money is the order's (Succeeded) and the order-level
    /// <see cref="RefundRequested"/> sends it back.
    /// </summary>
    private PaymentWebhookOutcome MarkUnfulfillable(Order order, PaymentAttempt attempt, string reason)
    {
        order.MarkUnfulfillable(reason);
        attempt.MarkSucceeded();
        LogRefund(order.Id, attempt.Id, reason);
        return PaymentWebhookOutcome.RefundPending;
    }

    /// <summary>Only this session's money goes back (<see cref="PaymentRefundRequested"/>); the order and its tickets stay as they are.</summary>
    private PaymentWebhookOutcome RefundSession(Order order, PaymentAttempt attempt, string reason, Money amount)
    {
        attempt.RequestRefund(reason, amount);
        LogRefund(order.Id, attempt.Id, reason);
        return PaymentWebhookOutcome.RefundPending;
    }

    /// <summary>Out-of-order safe: only a pending attempt fails, and the order only when nothing else can still pay it.</summary>
    private async Task<PaymentWebhookOutcome> FailedAsync(Order order, PaymentAttempt attempt, DateTimeOffset now, CancellationToken ct)
    {
        if (attempt.Status != PaymentAttemptStatus.Pending)
        {
            return PaymentWebhookOutcome.Ignored;
        }

        attempt.MarkFailed();
        var anotherLive = await db.PaymentAttempts.AnyAsync(
            a => a.OrderId == order.Id && a.Id != attempt.Id && a.Status == PaymentAttemptStatus.Pending && a.ExpiresAt > now, ct);
        if (order.Status == OrderStatus.AwaitingPayment && !anotherLive)
        {
            order.MarkPaymentFailed();
        }

        return PaymentWebhookOutcome.Failed;
    }

    [LoggerMessage(EventId = 8101, Level = LogLevel.Information, Message = "Order {OrderId} paid through attempt {AttemptId}: {TicketCount} tickets issued.")]
    private partial void LogPaid(Guid orderId, Guid attemptId, int ticketCount);

    [LoggerMessage(EventId = 8102, Level = LogLevel.Warning, Message = "Refund requested for order {OrderId}, attempt {AttemptId}: {Reason}.")]
    private partial void LogRefund(Guid orderId, Guid attemptId, string reason);

    [LoggerMessage(EventId = 8103, Level = LogLevel.Warning, Message = "Payment webhook {ProviderEventId} could not claim the order's inventory ({Reason}); deciding again without claiming.")]
    private partial void LogUnfulfillable(string providerEventId, InventoryConflictReason reason);

    [LoggerMessage(EventId = 8104, Level = LogLevel.Warning, Message = "Payment webhook {ProviderEventId} collided with a concurrent change (retry {Retry}).")]
    private partial void LogRetry(string providerEventId, int retry, Exception exception);

    [LoggerMessage(EventId = 8105, Level = LogLevel.Error, Message = "Payment webhook {ProviderEventId} from {Provider} names a session no payment attempt has; answered 200 without changes.")]
    private partial void LogUnknownSession(string provider, string providerEventId);
}
