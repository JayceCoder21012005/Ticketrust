using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Holds;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Waitlist;

/// <summary>Reads shared by the waitlist use cases.</summary>
internal static class WaitlistReads
{
    public static Task<WaitlistEntry?> LiveEntryAsync(IAppDbContext db, Guid userId, Guid eventId, CancellationToken ct) =>
        db.WaitlistEntries.FirstOrDefaultAsync(
            e => e.EventId == eventId && e.UserId == userId && (e.Status == WaitlistStatus.Waiting || e.Status == WaitlistStatus.Offered), ct);

    public static async Task<WaitlistStatusResponse> StatusAsync(IAppDbContext db, WaitlistEntry entry, CancellationToken ct)
    {
        int? position = entry.Status == WaitlistStatus.Waiting
            ? 1 + await db.WaitlistEntries.CountAsync(
                e => e.EventId == entry.EventId && e.Status == WaitlistStatus.Waiting
                    && (e.QueuedAt < entry.QueuedAt || (e.QueuedAt == entry.QueuedAt && e.Id.CompareTo(entry.Id) < 0)),
                ct)
            : null;
        var offer = entry is { Status: WaitlistStatus.Offered, OfferExpiresAt: { } expiresAt } ? new WaitlistOfferSummary(entry.Id, expiresAt) : null;
        return new WaitlistStatusResponse(entry.Id, entry.EventId, entry.Status, position, entry.TierId, entry.Quantity, entry.CreatedAt, offer);
    }
}

/// <summary>
/// <c>POST /events/{id}/waitlist</c> (spec 13): only for a published event that has not started, when it is sold out (nothing is
/// free; held counts as taken) or every sale phase has closed. One live entry per (event, user): the partial unique index settles
/// a race, and the loser gets <c>waitlist.already_joined</c>.
/// </summary>
public sealed class JoinWaitlistHandler(IAppDbContext db, IHoldCatalog catalog, IInventoryStore store, TimeProvider time)
{
    public async Task<Result<WaitlistStatusResponse>> HandleAsync(JoinWaitlistCommand command, CancellationToken ct)
    {
        if (!command.EmailVerified)
        {
            return WaitlistErrors.EmailNotConfirmed;
        }

        if (command.Quantity is < WaitlistEntry.MinQuantity or > WaitlistEntry.MaxQuantity)
        {
            return WaitlistErrors.InvalidQuantity;
        }

        var now = time.GetUtcNow();
        var startsAt = await db.Events.AsNoTracking()
            .Where(e => e.Id == command.EventId && e.Status == EventStatus.Published)
            .Select(e => (DateTimeOffset?)e.StartsAt)
            .FirstOrDefaultAsync(ct);
        if (startsAt is null || await catalog.GetAsync(command.EventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        if (command.TierId is { } tier && entry.Tiers.All(t => t.Id != tier))
        {
            return WaitlistErrors.InvalidTier;
        }

        if (await WaitlistReads.LiveEntryAsync(db, command.UserId, command.EventId, ct) is not null)
        {
            return WaitlistErrors.AlreadyJoined;
        }

        var open = await IsOpenAsync(entry, startsAt.Value, now, ct);
        if (open.IsFailure)
        {
            return open.Error;
        }

        if (await LiveTicketsAsync(command, ct) + command.Quantity > MaxTicketsPerUser(entry))
        {
            return WaitlistErrors.LimitExceeded;
        }

        var joined = WaitlistEntry.Join(command.EventId, command.UserId, command.TierId, command.Quantity, now);
        db.WaitlistEntries.Add(joined);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await WaitlistReads.LiveEntryAsync(db, command.UserId, command.EventId, ct) is null)
            {
                throw;
            }

            return WaitlistErrors.AlreadyJoined;
        }

        return await WaitlistReads.StatusAsync(db, joined, ct);
    }

    private async Task<Result> IsOpenAsync(HoldCatalogEntry entry, DateTimeOffset startsAt, DateTimeOffset now, CancellationToken ct)
    {
        if (now >= startsAt || entry.Phases.Count == 0)
        {
            return WaitlistErrors.NotAvailable;
        }

        if (entry.Phases.All(p => now >= p.ClosesAt))
        {
            return Result.Success();
        }

        try
        {
            var counts = await store.GetCountsAsync(entry.EventId, entry.GaTierIds, ct);
            return counts.Warm && counts.Available == 0 ? Result.Success() : WaitlistErrors.NotAvailable;
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }

    private Task<int> LiveTicketsAsync(JoinWaitlistCommand command, CancellationToken ct) =>
        db.Tickets.CountAsync(t => t.EventId == command.EventId && t.OwnerUserId == command.UserId && t.Status != TicketStatus.Voided, ct);

    private static int MaxTicketsPerUser(HoldCatalogEntry entry) => entry.Phases.Max(p => p.MaxTicketsPerUser);
}

/// <summary><c>GET /events/{id}/waitlist/me</c>: the caller's live entry, or 404 <c>waitlist.not_joined</c>.</summary>
public sealed class GetMyWaitlistEntryHandler(IAppDbContext db)
{
    public async Task<Result<WaitlistStatusResponse>> HandleAsync(Guid userId, Guid eventId, CancellationToken ct) =>
        await WaitlistReads.LiveEntryAsync(db, userId, eventId, ct) is { } entry
            ? await WaitlistReads.StatusAsync(db, entry, ct)
            : WaitlistErrors.NotJoined;
}

/// <summary><c>DELETE /events/{id}/waitlist/me</c>: idempotent. A live offer's hold goes to the next in line right away.</summary>
public sealed class LeaveWaitlistHandler(IAppDbContext db, WaitlistOffers offers)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        if (await WaitlistReads.LiveEntryAsync(db, userId, eventId, ct) is not { } entry)
        {
            return Result.Success();
        }

        var holdId = entry.OfferHoldId;
        var listingId = entry.OfferListingId;
        entry.Leave();
        if (listingId is { } offeredListing
            && await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == offeredListing && l.OfferedToUserId == userId, ct) is { } listing)
        {
            // Spec 14: the waiter gave up the window, so the public sees the listing at once.
            listing.OpenToPublic();
        }
        try
        {
            if (holdId is { } offered)
            {
                await offers.WithdrawAsync(entry, offered, ct);
            }
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }

        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary><c>GET /me/waitlist</c>: the caller's live entries, soonest event first.</summary>
public sealed class ListMyWaitlistHandler(IAppDbContext db)
{
    public async Task<IReadOnlyList<MyWaitlistItem>> HandleAsync(Guid userId, CancellationToken ct)
    {
        var entries = await db.WaitlistEntries.AsNoTracking()
            .Where(e => e.UserId == userId && (e.Status == WaitlistStatus.Waiting || e.Status == WaitlistStatus.Offered))
            .ToListAsync(ct);
        var eventIds = entries.Select(e => e.EventId).Distinct().ToList();
        var events = await db.Events.AsNoTracking().Include(e => e.Tiers).Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);

        var items = new List<MyWaitlistItem>(entries.Count);
        foreach (var entry in entries.Where(e => events.ContainsKey(e.EventId)))
        {
            var evt = events[entry.EventId];
            var tierName = entry.TierId is { } tier ? evt.Tiers.FirstOrDefault(t => t.Id == tier)?.Name : null;
            items.Add(new MyWaitlistItem(await WaitlistReads.StatusAsync(db, entry, ct), evt.Slug, evt.Title, evt.StartsAt, evt.City, evt.CoverImageUrl, tierName));
        }

        return [.. items.OrderBy(i => i.StartsAt)];
    }
}

/// <summary>
/// <c>GET /waitlist/offers/{id}</c> (the id is the entry's): the offered hold, priced. 404 for someone else's; 409
/// <c>waitlist.offer_expired</c> once it is no longer live.
/// </summary>
public sealed class GetWaitlistOfferHandler(IAppDbContext db, IHoldCatalog catalog, IInventoryStore store, TimeProvider time)
{
    public async Task<Result<WaitlistOfferResponse>> HandleAsync(Guid userId, Guid offerId, CancellationToken ct)
    {
        var entry = await db.WaitlistEntries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == offerId && e.UserId == userId, ct);
        if (entry is null)
        {
            return WaitlistErrors.OfferNotFound;
        }

        var now = time.GetUtcNow();
        var evt = await db.Events.AsNoTracking().Where(e => e.Id == entry.EventId).Select(e => new { e.Title, e.StartsAt, e.Slug }).FirstOrDefaultAsync(ct);
        if (entry is { Status: WaitlistStatus.Offered, OfferListingId: { } listingId, OfferExpiresAt: { } windowEnds } && evt is not null)
        {
            return await ListingOfferAsync(entry, listingId, windowEnds, evt.Slug, evt.Title, evt.StartsAt, now, ct);
        }
        if (entry is not { Status: WaitlistStatus.Offered, OfferHoldId: { } holdId } || evt is null || await catalog.GetAsync(entry.EventId, ct) is not { } catalogEntry)
        {
            return WaitlistErrors.OfferExpired;
        }

        try
        {
            if (await store.GetUserHoldAsync(entry.EventId, userId, ct) is not { } hold || hold.HoldId != holdId)
            {
                return WaitlistErrors.OfferExpired;
            }

            var priced = HoldPricing.ToResponse(catalogEntry, hold.HoldId, hold.Seats, hold.Ga, hold.ExpiresAt, now);
            return new WaitlistOfferResponse(entry.Id, entry.EventId, catalogEntry.Slug, evt.Title, evt.StartsAt, hold.ExpiresAt, priced);
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }

    /// <summary>
    /// A resale listing offered during its waitlist window (spec 14): live while the window lasts, or while the waiter's own resale
    /// order still reserves it.
    /// </summary>
    private async Task<Result<WaitlistOfferResponse>> ListingOfferAsync(
        WaitlistEntry entry, Guid listingId, DateTimeOffset windowEnds, string slug, string title, DateTimeOffset startsAt, DateTimeOffset now, CancellationToken ct)
    {
        var listing = await db.ResaleListings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null)
        {
            return WaitlistErrors.OfferExpired;
        }

        var open = listing.Status == ResaleListingStatus.Active && now < windowEnds && listing.IsOpenTo(entry.UserId, now);
        var reservedByThem = listing.Status == ResaleListingStatus.Reserved && await OwnOpenOrderAsync(listing.Id, entry.UserId, ct);
        if (!open && !reservedByThem)
        {
            return WaitlistErrors.OfferExpired;
        }

        var tier = await db.Events.AsNoTracking().Where(e => e.Id == entry.EventId).SelectMany(e => e.Tiers)
            .Where(t => t.Id == listing.TierId).Select(t => t.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        string? seat = null;
        if (listing.SeatIndex is { } index)
        {
            var labels = await Orders.SeatLabels.LoadAsync(db, [entry.EventId], [index], ct);
            seat = labels.TryGetValue((entry.EventId, index), out var label) ? $"{label.Section} · {label.Row} · {label.Number}" : null;
        }

        var offer = new WaitlistListingOffer(listing.Id, listing.TierId, tier, seat, listing.Price.Amount, listing.Price.Currency);
        return new WaitlistOfferResponse(entry.Id, entry.EventId, slug, title, startsAt, windowEnds, null, offer);
    }

    private Task<bool> OwnOpenOrderAsync(Guid listingId, Guid userId, CancellationToken ct) =>
        db.Orders.AsNoTracking().AnyAsync(
            o => o.HoldId == listingId && o.UserId == userId
                && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed),
            ct);
}

/// <summary>
/// <c>POST /waitlist/offers/{id}/accept</c>: checks the offer is still live and answers what checkout needs. No admission token:
/// orders are guarded by the user's live hold, which the waitlist already made (spec 08).
/// </summary>
public sealed class AcceptWaitlistOfferHandler(GetWaitlistOfferHandler offers, Resale.ReserveListingHandler reserve)
{
    public async Task<Result<AcceptedWaitlistOffer>> HandleAsync(Guid userId, bool emailVerified, Guid offerId, CancellationToken ct)
    {
        var offer = await offers.HandleAsync(userId, offerId, ct);
        if (offer.IsFailure)
        {
            return offer.Error;
        }

        var value = offer.Value;
        if (value.Listing is not { } listing)
        {
            return new AcceptedWaitlistOffer(value.Hold!.HoldId, value.EventId, value.EventSlug, value.ExpiresAt, null);
        }

        // A listing offer (spec 14): reserving it makes the resale order that checkout pays.
        var order = await reserve.HandleAsync(new Resale.ReserveListingCommand(userId, emailVerified, listing.ListingId), ct);
        return order.IsFailure
            ? order.Error
            : new AcceptedWaitlistOffer(null, value.EventId, value.EventSlug, order.Value.ExpiresAt, order.Value.Id);
    }
}
