using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

/// <summary>
/// <c>POST /orders/{id}/pay</c> (spec 08): hands the buyer a gateway page for their order. A live
/// <see cref="PaymentAttempt"/> is reused, so a double click gets the same URL. Otherwise: check in the database that
/// the event is still on sale, check that the buyer's hold is still live, open the gateway session, then in ONE save
/// move the order to <see cref="OrderStatus.AwaitingPayment"/> and insert the attempt.
/// <para>
/// Crash safety: the session is opened first with a key derived from the order and the attempt number (not from the HTTP
/// Idempotency-Key), so a retry after a crash, a second tab or a different header all converge on the same session.
/// An order is never AwaitingPayment without its attempt, and a gateway outage leaves the order and the hold untouched.
/// Concurrent requests race on the order's <c>xmin</c> and <c>ux_payment_attempts_provider_session_id</c>; the loser
/// reloads and answers from what the winner committed. The return URL is built here from configuration, never taken from the client.
/// </para>
/// </summary>
public sealed class PayOrderHandler(
    IAppDbContext db,
    IPaymentGatewayResolver gateways,
    IInventoryStore store,
    IOptions<FrontendOptions> frontend,
    TimeProvider time)
{
    public Task<Result<PayOrderResponse>> HandleAsync(Guid userId, Guid orderId, CancellationToken ct) =>
        HandleAsync(userId, orderId, retried: false, ct);

    private async Task<Result<PayOrderResponse>> HandleAsync(Guid userId, Guid orderId, bool retried, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var now = time.GetUtcNow();
        if (RefusalFor(order, now) is { } refusal)
        {
            return refusal;
        }

        // Redis and the catalog cache cannot see an event cancelled a moment ago; the database decides.
        if (!await db.Events.AsNoTracking().AnyAsync(e => e.Id == order.EventId && e.Status == EventStatus.Published, ct))
        {
            return OrderErrors.NotPayable;
        }

        if (order.Status == OrderStatus.AwaitingPayment && await FindLiveAttemptAsync(order.Id, now, ct) is { } live)
        {
            return ToResponse(live, now);
        }

        var started = await StartAsync(order, now, retried, ct);
        return started ?? await HandleAsync(userId, orderId, retried: true, ct);
    }

    private static Error? RefusalFor(Order order, DateTimeOffset now) => order.Status switch
    {
        OrderStatus.Pending or OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed => now >= order.ExpiresAt ? OrderErrors.Expired : null,
        _ => OrderErrors.NotPayable,
    };

    private Task<PaymentAttempt?> FindLiveAttemptAsync(Guid orderId, DateTimeOffset now, CancellationToken ct) =>
        db.PaymentAttempts.AsNoTracking()
            .Where(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.Pending && a.ExpiresAt > now)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <returns>The answer, or null when a concurrent request changed the order first and the caller should look again.</returns>
    private async Task<Result<PayOrderResponse>?> StartAsync(Order order, DateTimeOffset now, bool retried, CancellationToken ct)
    {
        if (order.ExpiresAt - PaymentAttempt.GatewayMargin <= now)
        {
            return OrderErrors.Expired;
        }

        if (await CheckHoldAsync(order, now, ct) is { } holdError)
        {
            return holdError;
        }

        var gateway = gateways.Default;
        var number = await db.PaymentAttempts.CountAsync(a => a.OrderId == order.Id, ct) + 1;
        var session = await OpenSessionAsync(gateway, order, number, ct);
        return session.IsFailure ? session.Error : await CommitAsync(order, gateway, session.Value, now, retried, ct);
    }

    /// <summary>The buyer's seats must still be theirs: a dead hold means the seats may already be someone else's.</summary>
    private async Task<Error?> CheckHoldAsync(Order order, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var hold = await store.GetUserHoldAsync(order.EventId, order.UserId, ct);
            return hold is not null && hold.HoldId == order.HoldId && hold.ExpiresAt > now ? null : OrderErrors.HoldExpired;
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }

    /// <summary>Same order and attempt number give the same key, hence the same session, however often this is called.</summary>
    private async Task<Result<PaymentSession>> OpenSessionAsync(IPaymentGateway gateway, Order order, int number, CancellationToken ct)
    {
        var returnUrl = new Uri($"{frontend.Value.BaseUrl.TrimEnd('/')}/checkout/{order.Id}/result");
        try
        {
            return await gateway.CreateSessionAsync(order, returnUrl, order.ExpiresAt - PaymentAttempt.GatewayMargin, $"order:{order.Id:N}:attempt:{number}", ct);
        }
        catch (PaymentGatewayUnavailableException)
        {
            return PaymentErrors.GatewayUnavailable;
        }
        catch (PaymentRejectedException)
        {
            return PaymentErrors.Rejected;
        }
    }

    private async Task<Result<PayOrderResponse>?> CommitAsync(
        Order order, IPaymentGateway gateway, PaymentSession session, DateTimeOffset now, bool retried, CancellationToken ct)
    {
        try
        {
            if (order.Status != OrderStatus.AwaitingPayment)
            {
                order.StartPayment(now);
            }

            var attempt = PaymentAttempt.Create(order.Id, gateway.Provider, session.SessionId, session.RedirectUrl.AbsoluteUri, order.Total, order.ExpiresAt, now);
            db.PaymentAttempts.Add(attempt);
            await db.SaveChangesAsync(ct);
            return ToResponse(attempt, now);
        }
        catch (DomainException ex) when (ex.Code is "order.expired" or "payment.window_closed")
        {
            return OrderErrors.Expired;
        }
        catch (DbUpdateException) when (!retried)
        {
            // A concurrent pay (or cancel/expiry) got there first: forget our changes and answer from what it committed.
            db.ChangeTracker.Clear();
            return null;
        }
    }

    private static PayOrderResponse ToResponse(PaymentAttempt attempt, DateTimeOffset now) =>
        new(attempt.RedirectUrl, attempt.Id, attempt.ExpiresAt, now);
}
