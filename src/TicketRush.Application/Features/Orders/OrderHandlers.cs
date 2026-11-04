using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Holds;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

/// <summary><c>GET /orders/{id}</c>: the buyer's own order. Someone else's order and an unknown id are both 404 (no id probing).</summary>
public sealed class GetOrderHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result<OrderResponse>> HandleAsync(Guid userId, Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct);
        return order is null ? OrderErrors.NotFound : await new OrderViews(db, time).ToResponseAsync(order, ct);
    }
}

/// <summary>
/// <c>POST /orders/{id}/cancel</c> (spec 08): <see cref="OrderStatus.Pending"/> → <see cref="OrderStatus.Cancelled"/> and the
/// hold goes back to the pool through the same code path as <c>DELETE holds/me</c>. Cancel is legal from Pending only, so a
/// cancelled order has never taken money; once payment started the answer is 409 <c>order.not_payable</c>. Cancelling a
/// cancelled order succeeds and releases the hold again, so a retry after Redis failed finishes the job.
/// </summary>
public sealed class CancelOrderHandler(IAppDbContext db, ReleaseMyHoldHandler release, TimeProvider time)
{
    public async Task<Result<OrderResponse>> HandleAsync(Guid userId, Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct);
        if (order is null)
        {
            return OrderErrors.NotFound;
        }

        var cancelled = await EnsureCancelledAsync(order, ct);
        if (cancelled.IsFailure)
        {
            return cancelled.Error;
        }

        var released = await ReleaseHoldAsync(cancelled.Value, ct);
        return released.IsFailure ? released.Error : await new OrderViews(db, time).ToResponseAsync(cancelled.Value, ct);
    }

    /// <summary>Pending is cancelled now; Cancelled stays as it is (idempotent); any other state is not cancellable.</summary>
    private async Task<Result<Order>> EnsureCancelledAsync(Order order, CancellationToken ct) => order.Status switch
    {
        OrderStatus.Pending => await CancelAsync(order, ct),
        OrderStatus.Cancelled => order,
        _ => OrderErrors.NotPayable,
    };

    /// <summary>
    /// Only this order's own hold: a newer hold the buyer made since is not ours to release. A cancelled event has no
    /// catalog (and its holds were already released), so there is nothing left to do for it.
    /// </summary>
    private async Task<Result> ReleaseHoldAsync(Order order, CancellationToken ct)
    {
        var released = await release.HandleAsync(order.UserId, order.EventId, order.HoldId, ct);
        return released.IsFailure && released.Error.Code != EventErrors.NotFound.Code ? released : Result.Success();
    }

    private async Task<Result<Order>> CancelAsync(Order order, CancellationToken ct)
    {
        order.Cancel();
        try
        {
            await db.SaveChangesAsync(ct);
            return order;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Payment started (or the sweep expired it) between the read and the write: whoever got there first wins.
            db.ChangeTracker.Clear();
            var current = await db.Orders.FirstOrDefaultAsync(o => o.Id == order.Id, ct);
            return current is { Status: OrderStatus.Cancelled } ? current : OrderErrors.NotPayable;
        }
    }
}

public sealed class ListMyOrdersValidator : AbstractValidator<ListMyOrdersQuery>
{
    public ListMyOrdersValidator()
    {
        RuleFor(q => q.Cursor)
            .Must(cursor => string.IsNullOrWhiteSpace(cursor) || OrderCursor.TryDecode(cursor.Trim(), out _))
            .WithMessage("Invalid cursor.");
        RuleFor(q => q.Limit).InclusiveBetween(1, ListMyOrdersQuery.MaxLimit);
    }
}

/// <summary><c>GET /me/orders</c>: newest first, keyset-paged on (created_at, id) so rows with the same instant never repeat or vanish.</summary>
public sealed class ListMyOrdersHandler(IAppDbContext db)
{
    public async Task<OrderPage> HandleAsync(ListMyOrdersQuery query, CancellationToken ct)
    {
        var orders = db.Orders.AsNoTracking().Where(o => o.UserId == query.UserId);
        if (!string.IsNullOrWhiteSpace(query.Cursor) && OrderCursor.TryDecode(query.Cursor.Trim(), out var after))
        {
            orders = orders.Where(o => o.CreatedAt < after.CreatedAt || (o.CreatedAt == after.CreatedAt && o.Id.CompareTo(after.Id) < 0));
        }

        // Ordering and paging happen in SQL on the keyset index; events are looked up for just this page.
        var rows = await orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Take(query.Limit + 1)
            .Select(o => new { o.Id, o.EventId, o.Status, Total = o.Total.Amount, ItemCount = o.Items.Sum(i => i.Quantity), o.CreatedAt, o.PaidAt })
            .ToListAsync(ct);
        var page = rows.Take(query.Limit).ToList();
        var eventIds = page.Select(r => r.EventId).Distinct().ToList();
        var events = await db.Events.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Title, e.Slug, e.StartsAt })
            .ToDictionaryAsync(e => e.Id, ct);

        var items = page
            .Select(r => new OrderSummary(r.Id, r.EventId, events[r.EventId].Title, events[r.EventId].Slug, events[r.EventId].StartsAt, r.Status, r.Total, r.ItemCount, r.CreatedAt, r.PaidAt))
            .ToList();
        var next = rows.Count > query.Limit ? OrderCursor.Encode(page[^1].CreatedAt, page[^1].Id) : null;
        return new OrderPage(items, next);
    }
}
