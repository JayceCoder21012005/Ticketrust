using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Resale;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Features.Resale;

/// <summary>
/// The ownership change of spec 14, inside the caller's transaction: the old ticket (Listed) is voided and that is SAVED before the
/// new owner's ticket is added, so the partial unique seat index never sees two live tickets. The old row's <c>xmin</c> makes a
/// concurrent change (a cancel, a second hand-over) fail the transaction instead of slipping through.
/// </summary>
internal static class OwnershipChange
{
    public static async Task<Ticket> HandOverAsync(IAppDbContext db, Ticket ticket, Guid orderId, Guid newOwnerUserId, CancellationToken ct)
    {
        ticket.HandOver();
        await db.SaveChangesAsync(ct);
        var reissued = ticket.Reissue(orderId, newOwnerUserId);
        db.Tickets.Add(reissued);
        return reissued;
    }
}

/// <summary>What pay (08), fulfil (08) and expiry/cancel do differently for a <see cref="OrderKind.Resale"/> order.</summary>
public sealed class ResaleSettlement(IAppDbContext db, IResaleReservations reservations)
{
    /// <summary>Pay: the listing must still be reserved by this order, and the reservation not over.</summary>
    public async Task<bool> IsReservedAsync(Order order, DateTimeOffset now, CancellationToken ct) =>
        order.ExpiresAt > now
        && await db.ResaleListings.AsNoTracking().AnyAsync(
            l => l.Id == order.ListingId && l.Status == ResaleListingStatus.Reserved && l.ReservedOrderId == order.Id, ct);

    /// <summary>
    /// Fulfil, inside the webhook's transaction: listing → Sold (reserved by this order, or still active after a late payment), the
    /// seller's ticket handed over to the buyer, and the seller's payout. Anything else throws
    /// <see cref="InventoryConflictException"/> so the order is refunded (<c>seats_unavailable</c>).
    /// </summary>
    public async Task CompleteAsync(Order order, CancellationToken ct)
    {
        var listing = await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == order.ListingId, ct)
            ?? throw new InvalidOperationException($"Resale order {order.Id} has no listing.");
        try
        {
            listing.MarkSold(order.Id);
        }
        catch (Domain.Primitives.DomainException)
        {
            throw new InventoryConflictException(InventoryConflictReason.SeatTaken, "The listing was sold to someone else or withdrawn.");
        }

        var ticket = await db.Tickets.FirstOrDefaultAsync(
            t => t.Id == listing.TicketId && t.OwnerUserId == listing.SellerId && t.Status == TicketStatus.Listed, ct)
            ?? throw new InventoryConflictException(InventoryConflictReason.SeatTaken, "The listed ticket is no longer listed.");

        await OwnershipChange.HandOverAsync(db, ticket, order.Id, order.UserId, ct);
        db.SellerPayouts.Add(SellerPayout.Create(listing));
    }

    /// <summary>
    /// Expiry or cancel: the listing goes back on sale if this order still reserves it, and the Redis key is freed if still the
    /// buyer's (best effort: it expires by itself). The caller saves.
    /// </summary>
    public async Task ReleaseAsync(Order order, CancellationToken ct)
    {
        var listing = await db.ResaleListings.FirstOrDefaultAsync(l => l.Id == order.ListingId, ct);
        if (listing is null || !listing.ReleaseReservation(order.Id))
        {
            return;
        }

        try
        {
            await reservations.ReleaseAsync(listing.EventId, listing.Id, order.UserId, ct);
        }
        catch (InventoryUnavailableException)
        {
            // The key dies with its 10-minute TTL; until then nobody else can reserve, which is only a short delay.
        }
    }
}
