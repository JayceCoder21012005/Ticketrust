using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Events;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Resale;

/// <summary>
/// A new listing goes to the waitlist first (spec 13/14, approach A): the oldest Waiting entry that wants one ticket of its tier gets
/// it alone until the window ends; with nobody waiting (or the event already past the resale cutoff) it is public at once. The
/// outbox commits these changes with its receipt.
/// </summary>
public sealed class ListingCreatedWaitlistHandler(IAppDbContext db, TimeProvider time) : IIntegrationEventHandler<ListingCreated>
{
    public async Task HandleAsync(ListingCreated message, IntegrationEventContext context, CancellationToken ct)
    {
        var listing = await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == message.ListingId, ct);
        if (listing is not { Status: ResaleListingStatus.Active, WaitlistUntil: { } until, OfferedToUserId: null })
        {
            return;
        }

        var now = time.GetUtcNow();
        var waiter = until > now && await ResaleReads.IsOpenAsync(db, listing.EventId, now, ct)
            ? await db.WaitlistEntries
                .Where(e => e.EventId == listing.EventId && e.Status == WaitlistStatus.Waiting && e.UserId != listing.SellerId
                    && (e.TierId == null || e.TierId == listing.TierId) && e.Quantity <= 1)
                .OrderBy(e => e.QueuedAt)
                .ThenBy(e => e.Id)
                .FirstOrDefaultAsync(ct)
            : null;
        if (waiter is null)
        {
            listing.OpenToPublic();
            return;
        }

        waiter.OfferListing(listing.Id, until);
        listing.OfferTo(waiter.UserId);
    }
}

/// <summary>
/// The event was cancelled: its listings and pending transfers end (the cancellation already voided the tickets and refunds every
/// paid order, resale ones included), and pending payouts are cancelled since the seller's original order is refunded.
/// </summary>
public sealed class EventCancelledResaleHandler(IAppDbContext db) : IIntegrationEventHandler<EventCancelled>
{
    public async Task HandleAsync(EventCancelled message, IntegrationEventContext context, CancellationToken ct)
    {
        var listings = await db.ResaleListings
            .Where(l => l.EventId == message.EventId && (l.Status == ResaleListingStatus.Active || l.Status == ResaleListingStatus.Reserved))
            .ToListAsync(ct);
        listings.ForEach(l => l.CloseForCancelledEvent());

        var transfers = await db.TicketTransfers.Where(t => t.EventId == message.EventId && t.Status == TicketTransferStatus.Pending).ToListAsync(ct);
        transfers.ForEach(t => t.CloseForCancelledEvent());

        var payouts = await db.SellerPayouts.Where(p => p.EventId == message.EventId && p.Status == SellerPayoutStatus.Pending).ToListAsync(ct);
        payouts.ForEach(p => p.Cancel());
    }
}

/// <summary>
/// The worker's resale sweep (spec 14), one batch per call, each item saved on its own (a concurrent change skips it until the next
/// run): pending transfers past 48 h expire; at the cutoff (2 h before the start) active listings and pending transfers end so the
/// owner can still go; pending payouts of ended events are paid (mock).
/// </summary>
public sealed class ResaleSweepHandler(IAppDbContext db, TimeProvider time)
{
    public const int BatchSize = 100;

    /// <returns>How many items changed.</returns>
    public async Task<int> HandleAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cutoff = now + ResaleRules.Cutoff;
        var changed = 0;

        var transfers = await db.TicketTransfers
            .Where(t => t.Status == TicketTransferStatus.Pending
                && (t.ExpiresAt <= now || db.Events.Any(e => e.Id == t.EventId && e.StartsAt <= cutoff)))
            .OrderBy(t => t.ExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var transfer in transfers)
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == transfer.TicketId, ct);
            if (transfer.ExpiresAt <= now)
            {
                transfer.Expire(ticket);
            }
            else
            {
                transfer.Cancel(ticket);
            }

            changed += await TrySaveAsync(ct);
        }

        var listings = await db.ResaleListings
            .Where(l => l.Status == ResaleListingStatus.Active && db.Events.Any(e => e.Id == l.EventId && e.StartsAt <= cutoff))
            .OrderBy(l => l.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var listing in listings)
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == listing.TicketId, ct);
            listing.Cancel(ticket);
            var offered = await db.WaitlistEntries.FirstOrDefaultAsync(e => e.Status == WaitlistStatus.Offered && e.OfferListingId == listing.Id, ct);
            offered?.WithdrawOffer();
            changed += await TrySaveAsync(ct);
        }

        var payouts = await db.SellerPayouts
            .Where(p => p.Status == SellerPayoutStatus.Pending
                && db.Events.Any(e => e.Id == p.EventId && e.Status != EventStatus.Cancelled && e.EndsAt <= now))
            .OrderBy(p => p.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);
        foreach (var payout in payouts)
        {
            payout.MarkPaid(now);
            changed += await TrySaveAsync(ct);
        }

        return changed;
    }

    private async Task<int> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return 1;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Forget only this item's changes: the rest of the batch is still tracked and goes on.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged && e.State != EntityState.Detached).ToList())
            {
                entry.State = EntityState.Detached;
            }

            return 0;
        }
    }
}
