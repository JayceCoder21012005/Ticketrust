using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Holds;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

public sealed class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(c => c.HoldId).NotEmpty();
        RuleFor(c => c.Buyer.Name).NotEmpty().MaximumLength(100).Must(n => !string.IsNullOrWhiteSpace(n)).OverridePropertyName("buyer.name");
        RuleFor(c => c.Buyer.Email).NotEmpty().EmailAddress().MaximumLength(256).OverridePropertyName("buyer.email");
        RuleFor(c => c.Buyer.Phone).Matches(@"^(0|\+84)\d{9}$").When(c => c.Buyer.Phone is not null)
            .WithMessage("Phone number must be a Vietnamese mobile number.").OverridePropertyName("buyer.phone");
    }
}

/// <summary>
/// <c>POST /orders</c> (spec 08): turns the buyer's live hold into a <see cref="OrderStatus.Pending"/> order. Lines and
/// unit prices are copied from the hold's priced response, never from the live catalog. A hold has at most one open
/// order (<c>ux_orders_open_hold</c>): asking again returns that order, so a double submit cannot create two.
/// </summary>
public sealed class CreateOrderHandler(
    IAppDbContext db, IHoldCatalog catalog, IInventoryStore store, Abstractions.Dashboard.IDashboardStore dashboard, TimeProvider time)
{
    public async Task<Result<CreatedOrder>> HandleAsync(CreateOrderCommand command, CancellationToken ct)
    {
        // Redis (and the cached catalog) cannot see an event cancelled a moment ago; the database decides.
        if (!await IsPublishedAsync(command.EventId, ct) || await catalog.GetAsync(command.EventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        var now = time.GetUtcNow();
        var hold = await FindLiveHoldAsync(command, now, ct);
        if (hold.IsFailure)
        {
            return hold.Error;
        }

        var views = new OrderViews(db, time);
        if (await FindOpenOrderAsync(command.HoldId, command.UserId, ct) is { } existing)
        {
            return new CreatedOrder(await views.ToResponseAsync(existing, ct), false);
        }

        // Invariant: the catalog is cached, so these prices equal what the hold showed ONLY because tier prices cannot change
        // after publish (Event.ReplaceTiers is Draft-only). Anyone allowing price edits on a published event must snapshot
        // the prices on the hold instead, or an order could be priced differently from what the buyer saw.
        var priced = HoldPricing.ToResponse(entry, hold.Value.HoldId, hold.Value.Seats, hold.Value.Ga, hold.Value.ExpiresAt, now);
        return await SaveAsync(command, priced, now, views, ct);
    }

    private Task<bool> IsPublishedAsync(Guid eventId, CancellationToken ct) =>
        db.Events.AsNoTracking().AnyAsync(e => e.Id == eventId && e.Status == EventStatus.Published, ct);

    private Task<Order?> FindOpenOrderAsync(Guid holdId, Guid userId, CancellationToken ct) =>
        db.Orders.AsNoTracking()
            .Where(o => o.HoldId == holdId && o.UserId == userId
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed))
            .FirstOrDefaultAsync(ct);

    private async Task<Result<ActiveHold>> FindLiveHoldAsync(CreateOrderCommand command, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var hold = await store.GetUserHoldAsync(command.EventId, command.UserId, ct);
            if (hold is null)
            {
                // The record of an expired hold lingers until the sweep, and an order for it proves it existed; anything else is not theirs.
                return await WasTheirHoldAsync(command, ct) ? OrderErrors.HoldExpired : OrderErrors.HoldInvalid;
            }

            if (hold.HoldId != command.HoldId)
            {
                return OrderErrors.HoldInvalid;
            }

            return hold.ExpiresAt <= now ? OrderErrors.HoldExpired : hold;
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }

    private async Task<bool> WasTheirHoldAsync(CreateOrderCommand command, CancellationToken ct) =>
        await store.HoldRecordedAsync(command.EventId, command.HoldId, command.UserId, ct)
        || await db.Orders.AsNoTracking().AnyAsync(o => o.HoldId == command.HoldId && o.UserId == command.UserId, ct);

    private async Task<Result<CreatedOrder>> SaveAsync(CreateOrderCommand command, HoldResponse priced, DateTimeOffset now, OrderViews views, CancellationToken ct)
    {
        var created = BuildOrder(command, priced, now);
        if (created.IsFailure)
        {
            return created.Error;
        }

        var order = created.Value;
        var winner = await TryInsertAsync(order, command, ct);
        if (winner is not null)
        {
            return new CreatedOrder(await views.ToResponseAsync(winner, ct), false);
        }

        // Spec 15's funnel step "order created" (best effort; the reconciler recounts it from Postgres).
        await dashboard.RecordOrderCreatedAsync(command.EventId, ct);
        return new CreatedOrder(await views.ToResponseAsync(order, ct), true);
    }

    private static Result<Order> BuildOrder(CreateOrderCommand command, HoldResponse priced, DateTimeOffset now)
    {
        try
        {
            return Order.Create(
                command.UserId,
                command.EventId,
                command.HoldId,
                ItemsOf(priced),
                new Money(priced.Total),
                new BuyerInfo(command.Buyer.Name, command.Buyer.Email, command.Buyer.Phone),
                priced.ExpiresAt,
                now);
        }
        catch (DomainException ex)
        {
            return Error.Conflict(ex.Code, ex.Message);
        }
    }

    /// <returns>Null when the order was inserted; the order that won the race for <c>ux_orders_open_hold</c> otherwise.</returns>
    private async Task<Order?> TryInsertAsync(Order order, CreateOrderCommand command, CancellationToken ct)
    {
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await FindOpenOrderAsync(command.HoldId, command.UserId, ct);

            if (winner is null)
            {
                // Nobody else has an open order for the hold: the failure is something else.
                throw;
            }

            return winner;
        }
    }

    private static List<OrderItem> ItemsOf(HoldResponse priced) =>
    [
        .. priced.Seats.Select(s => new OrderItem(s.TierId, s.Index, new Money(s.Price), 1)),
        .. priced.Ga.Select(g => new OrderItem(g.TierId, null, new Money(g.Price), g.Quantity)),
    ];
}
