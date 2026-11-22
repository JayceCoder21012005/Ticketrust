using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Resale;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Features.Tickets;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Resale;

/// <summary>Reads shared by the resale and transfer use cases.</summary>
internal static class ResaleReads
{
    /// <summary>The event's start when it is published and still inside the resale window (2 h before it starts).</summary>
    public static async Task<bool> IsOpenAsync(IAppDbContext db, Guid eventId, DateTimeOffset now, CancellationToken ct)
    {
        var startsAt = await db.Events.AsNoTracking()
            .Where(e => e.Id == eventId && e.Status == EventStatus.Published)
            .Select(e => (DateTimeOffset?)e.StartsAt)
            .FirstOrDefaultAsync(ct);
        return startsAt is { } start && ResaleRules.IsOpen(start, now);
    }

    public static Task<Money?> FaceValueAsync(IAppDbContext db, Guid eventId, Guid tierId, CancellationToken ct) =>
        db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Tiers).Where(t => t.Id == tierId).Select(t => t.Price).FirstOrDefaultAsync(ct);

    public static async Task<string?> SeatLabelAsync(IAppDbContext db, Guid eventId, int? seatIndex, CancellationToken ct)
    {
        if (seatIndex is not { } index)
        {
            return null;
        }

        var labels = await SeatLabels.LoadAsync(db, [eventId], [index], ct);
        return SeatLabelText.Of(labels.GetValueOrDefault((eventId, index)));
    }

    public static async Task<ListingResponse> ToResponseAsync(IAppDbContext db, ResaleListing listing, CancellationToken ct) =>
        new(listing.Id, listing.TicketId, listing.EventId, listing.TierId, await SeatLabelAsync(db, listing.EventId, listing.SeatIndex, ct),
            listing.Price.Amount, listing.Price.Currency, listing.Status, listing.WaitlistUntil, listing.CreatedAt);
}

public sealed record CreateListingCommand(Guid UserId, Guid TicketId, long Price);

/// <summary>
/// <c>POST /tickets/{id}/listings</c> (spec 14): the owner lists a Valid ticket at 1 to face value until 2 h before the start. The
/// ticket's <c>xmin</c> (and the one-open-listing index) settles a race with a second listing or a transfer of the same ticket.
/// </summary>
public sealed class CreateListingHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result<ListingResponse>> HandleAsync(CreateListingCommand command, CancellationToken ct)
    {
        if (command.Price < 1)
        {
            return ResaleErrors.InvalidPrice;
        }

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == command.TicketId && t.OwnerUserId == command.UserId, ct);
        if (ticket is null)
        {
            return TicketErrors.NotFound;
        }

        var now = time.GetUtcNow();
        if (ticket.Status != TicketStatus.Valid || !await ResaleReads.IsOpenAsync(db, ticket.EventId, now, ct))
        {
            return ResaleErrors.NotAllowed;
        }

        var face = await ResaleReads.FaceValueAsync(db, ticket.EventId, ticket.TierId, ct);
        if (face is null)
        {
            return ResaleErrors.NotAllowed;
        }

        ResaleListing listing;
        try
        {
            listing = ResaleListing.Create(ticket, new Money(command.Price, face.Currency), face, now);
        }
        catch (DomainException ex) when (ex.Code == "resale.price_above_cap")
        {
            return ResaleErrors.PriceAboveCap(face.Amount);
        }

        db.ResaleListings.Add(listing);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // DbUpdateConcurrencyException too: the ticket was listed, transferred or scanned a moment ago.
            db.ChangeTracker.Clear();
            return ResaleErrors.NotAllowed;
        }

        return await ResaleReads.ToResponseAsync(db, listing, ct);
    }
}

/// <summary>
/// <c>DELETE /listings/{id}</c>: the seller withdraws an Active listing; the ticket is Valid again. A waiter it was offered to goes
/// back to their place in line (no miss: nothing was theirs to lose).
/// </summary>
public sealed class CancelListingHandler(IAppDbContext db)
{
    public async Task<Result> HandleAsync(Guid userId, Guid listingId, CancellationToken ct)
    {
        var listing = await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == listingId && l.SellerId == userId, ct);
        if (listing is null)
        {
            return ResaleErrors.ListingNotFound;
        }

        if (listing.Status == ResaleListingStatus.Cancelled)
        {
            return Result.Success();
        }

        if (listing.Status != ResaleListingStatus.Active)
        {
            return ResaleErrors.ListingUnavailable;
        }

        var ticket = await db.Tickets.SingleAsync(t => t.Id == listing.TicketId, ct);
        listing.Cancel(ticket);
        var offered = await db.WaitlistEntries.FirstOrDefaultAsync(e => e.Status == WaitlistStatus.Offered && e.OfferListingId == listing.Id, ct);
        offered?.WithdrawOffer();
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Reserved (or cancelled) a moment ago.
            db.ChangeTracker.Clear();
            var current = await db.ResaleListings.AsNoTracking().FirstAsync(l => l.Id == listingId, ct);
            return current.Status == ResaleListingStatus.Cancelled ? Result.Success() : ResaleErrors.ListingUnavailable;
        }

        return Result.Success();
    }
}

/// <summary>
/// <c>GET /events/{id}/listings</c> (public): Active listings past their waitlist window, grouped by tier, cheapest first. Empty for an
/// event that is not published or past the resale cutoff.
/// </summary>
public sealed class ListEventListingsHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<ListingTierGroup>> HandleAsync(Guid eventId, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (!await ResaleReads.IsOpenAsync(db, eventId, now, ct))
        {
            return [];
        }

        var listings = await db.ResaleListings.AsNoTracking()
            .Where(l => l.EventId == eventId && l.Status == ResaleListingStatus.Active && (l.WaitlistUntil == null || l.WaitlistUntil <= now))
            .OrderBy(l => l.Price.Amount)
            .ThenBy(l => l.CreatedAt)
            .ToListAsync(ct);
        if (listings.Count == 0)
        {
            return [];
        }

        var tiers = await db.Events.AsNoTracking().Where(e => e.Id == eventId).SelectMany(e => e.Tiers)
            .Select(t => new { t.Id, t.Name, t.Color, t.Price })
            .ToListAsync(ct);
        var seats = listings.Where(l => l.SeatIndex is not null).Select(l => l.SeatIndex!.Value).Distinct().ToList();
        var labels = await SeatLabels.LoadAsync(db, [eventId], seats, ct);

        return [.. tiers
            .OrderByDescending(t => t.Price.Amount)
            .Select(t => new ListingTierGroup(t.Id, t.Name, t.Color, t.Price.Amount, [.. listings
                .Where(l => l.TierId == t.Id)
                .Select(l => new PublicListing(l.Id, l.SeatIndex is { } i ? SeatLabelText.Of(labels.GetValueOrDefault((eventId, i))) : null, l.Price.Amount, l.Price.Currency))]))
            .Where(g => g.Listings.Count > 0)];
    }
}

public sealed record ReserveListingCommand(Guid UserId, bool EmailVerified, Guid ListingId);

/// <summary>
/// <c>POST /listings/{id}/reserve</c> (spec 14): a 10-minute reservation of the listing for the buyer and a Pending resale order
/// to pay through 08. Redis <c>SET NX</c> first, then the row (Active → Reserved, <c>xmin</c>) in the same save as the order;
/// either losing → <c>resale.listing_unavailable</c>, and a lost row frees the Redis key again. The same buyer asking again gets
/// their open order back.
/// </summary>
public sealed class ReserveListingHandler(IAppDbContext db, IResaleReservations reservations, IUserDirectory users, TimeProvider time)
{
    public async Task<Result<OrderResponse>> HandleAsync(ReserveListingCommand command, CancellationToken ct)
    {
        if (!command.EmailVerified)
        {
            return ResaleErrors.EmailNotConfirmed;
        }

        var listing = await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == command.ListingId, ct);
        if (listing is null)
        {
            return ResaleErrors.ListingNotFound;
        }

        var views = new OrderViews(db, time);
        if (await OpenOrderAsync(listing.Id, command.UserId, ct) is { } open)
        {
            return await views.ToResponseAsync(open, ct);
        }

        var now = time.GetUtcNow();
        var checkedOut = await CheckAsync(listing, command.UserId, now, ct);
        if (checkedOut.IsFailure)
        {
            return checkedOut.Error;
        }

        if (await users.GetContactAsync(command.UserId, ct) is not { } contact)
        {
            return ResaleErrors.EmailNotConfirmed;
        }

        try
        {
            if (!await reservations.TryReserveAsync(listing.EventId, listing.Id, command.UserId, ResaleRules.ReservationTtl, ct))
            {
                return ResaleErrors.ListingUnavailable;
            }
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }

        return await SaveAsync(listing, command, contact, now, views, ct);
    }

    private async Task<Result> CheckAsync(ResaleListing listing, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        if (listing.SellerId == userId)
        {
            return ResaleErrors.OwnListing;
        }

        if (!await ResaleReads.IsOpenAsync(db, listing.EventId, now, ct))
        {
            return ResaleErrors.NotAllowed;
        }

        if (!listing.IsOpenTo(userId, now))
        {
            return ResaleErrors.ListingUnavailable;
        }

        // The webhook re-checks under the buyer's advisory lock (spec 12's cap); this only spares a payment that would be refunded.
        var limit = await db.Events.AsNoTracking().Where(e => e.Id == listing.EventId).SelectMany(e => e.Phases)
            .MaxAsync(p => (int?)p.MaxTicketsPerUser, ct) ?? SalePhase.MaxTicketsPerUserLimit;
        var owned = await db.Tickets.CountAsync(t => t.EventId == listing.EventId && t.OwnerUserId == userId && t.Status != TicketStatus.Voided, ct);
        return owned + 1 > limit ? ResaleErrors.LimitExceeded : Result.Success();
    }

    private async Task<Result<OrderResponse>> SaveAsync(
        ResaleListing listing, ReserveListingCommand command, UserContact contact, DateTimeOffset now, OrderViews views, CancellationToken ct)
    {
        var order = Order.CreateResale(
            command.UserId,
            listing.EventId,
            listing.Id,
            new OrderItem(listing.TierId, listing.SeatIndex, listing.Price, 1),
            new BuyerInfo(string.IsNullOrWhiteSpace(contact.FullName) ? contact.Email : contact.FullName, contact.Email, null),
            now + ResaleRules.ReservationTtl,
            now);
        listing.Reserve(order.Id, command.UserId, now);
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
            return await views.ToResponseAsync(order, ct);
        }
        catch (DbUpdateException)
        {
            // Another buyer's reservation (xmin) or this buyer's own concurrent request (one open order per listing) won.
            db.ChangeTracker.Clear();
            if (await OpenOrderAsync(listing.Id, command.UserId, ct) is { } mine)
            {
                return await views.ToResponseAsync(mine, ct);
            }

            await ReleaseQuietlyAsync(listing, command.UserId, ct);
            return ResaleErrors.ListingUnavailable;
        }
    }

    private async Task ReleaseQuietlyAsync(ResaleListing listing, Guid userId, CancellationToken ct)
    {
        try
        {
            await reservations.ReleaseAsync(listing.EventId, listing.Id, userId, ct);
        }
        catch (InventoryUnavailableException)
        {
            // The key expires on its own in 10 minutes; the row, not the key, decides who may pay.
        }
    }

    private Task<Order?> OpenOrderAsync(Guid listingId, Guid userId, CancellationToken ct) =>
        db.Orders.AsNoTracking()
            .Where(o => o.HoldId == listingId && o.UserId == userId && o.Kind == OrderKind.Resale
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed))
            .FirstOrDefaultAsync(ct);
}
