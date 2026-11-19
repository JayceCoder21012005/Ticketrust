using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Waitlist;

/// <summary>
/// Hands returned tickets to the waitlist (spec 13). A returned ticket becomes a 15-minute hold of the oldest matching
/// <see cref="WaitlistStatus.Waiting"/> entry in one Redis step, never free in between; an unused offer moves, as it is, to the
/// next matching entry, and only when nobody matches does it go back to the public sale. Database changes are saved by the
/// caller (the outbox commits them with its receipt; the expiry loop saves per entry).
/// </summary>
public sealed class WaitlistOffers(IAppDbContext db, IHoldCatalog catalog, IInventoryStore store, IOptions<WaitlistOptions> options, TimeProvider time)
{
    private const int CandidatesPerTicket = 20;
    private const int ExpiriesPerTick = 100;

    private TimeSpan Ttl => options.Value.OfferTtl;

    /// <summary>
    /// The hold id is the ticket id, so a redelivery after a crash between Redis and the commit finds its own hold and finishes the
    /// offer instead of making another (the outbox receipt covers redeliveries after the commit).
    /// </summary>
    public async Task OfferReturnedAsync(TicketReturned returned, CancellationToken ct)
    {
        if (await catalog.GetAsync(returned.EventId, ct) is not { } entry)
        {
            // Not published any more (cancelled): the cancellation settles that event's inventory.
            return;
        }

        int[] seats = returned.SeatIndex is { } seat ? [seat] : [];
        GaLine[] ga = returned.SeatIndex is null ? [new GaLine(returned.TierId, 1)] : [];
        var holdId = returned.TicketId;
        foreach (var candidate in await CandidatesAsync(returned.EventId, returned.TierId, 1, null, ct))
        {
            var outcome = await store.HoldReturnedAsync(returned.EventId, holdId, candidate.UserId, seats, ga, returned.PreviousOwnerUserId, Ttl, ct);
            switch (outcome.Status)
            {
                case WaitlistHoldStatus.Ok:
                    candidate.Offer(holdId, outcome.ExpiresAt!.Value);
                    return;
                case WaitlistHoldStatus.HasHold when await store.GetHoldAsync(returned.EventId, holdId, ct) is { } own && own.UserId == candidate.UserId:
                    candidate.Offer(holdId, own.ExpiresAt);
                    return;
                case WaitlistHoldStatus.HasHold:
                    // Busy buying something else for this event: the next one in line gets it.
                    continue;
                default:
                    // Taken: already handed to a waiter, or held since a reconcile freed it. Nothing left to give.
                    return;
            }
        }

        await store.FreeReturnedAsync(returned.EventId, seats, ga, returned.PreviousOwnerUserId, entry.GaTierIds, ct);
    }

    /// <summary>Offers whose time ran out: each misses once (back of the line) and its hold moves on. Saves per entry.</summary>
    /// <returns>How many offers were handled.</returns>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var due = await db.WaitlistEntries
            .Where(e => e.Status == WaitlistStatus.Offered && e.OfferExpiresAt <= now)
            .OrderBy(e => e.OfferExpiresAt)
            .Take(ExpiriesPerTick)
            .ToListAsync(ct);

        var handled = 0;
        foreach (var entry in due)
        {
            var holdId = entry.OfferHoldId!.Value;
            if (await HasOpenOrderAsync(holdId, ct))
            {
                // Checkout (08) is still deciding about a payment on this hold; its order expiry comes first.
                continue;
            }

            entry.Miss(now);
            await HandOnAsync(entry, holdId, ct);
            await db.SaveChangesAsync(ct);
            handled++;
        }

        return handled;
    }

    /// <summary>The user gave up a live offer (left the waitlist): the hold goes to the next waiter. The caller saves.</summary>
    public Task WithdrawAsync(WaitlistEntry entry, Guid holdId, CancellationToken ct) => HandOnAsync(entry, holdId, ct);

    /// <summary>Moves <paramref name="holdId"/> of <paramref name="from"/> to the next matching waiter, or back to the public sale.</summary>
    private async Task HandOnAsync(WaitlistEntry from, Guid holdId, CancellationToken ct)
    {
        var next = PassedOn(holdId);
        if (await store.GetHoldAsync(from.EventId, holdId, ct) is not { } hold)
        {
            await RecoverPassAsync(from, next, ct);
            return;
        }

        if (await catalog.GetAsync(from.EventId, ct) is not { } entry)
        {
            return;
        }

        var tiers = entry.Seats.Where(s => hold.Seats.Contains(s.Index)).Select(s => s.TierId).Concat(hold.Ga.Select(l => l.TierId)).Distinct().ToList();
        var units = hold.Seats.Count + hold.Ga.Sum(l => l.Quantity);
        if (tiers.Count == 1)
        {
            foreach (var candidate in await CandidatesAsync(from.EventId, tiers[0], units, from.Id, ct))
            {
                var outcome = await store.PassHoldAsync(from.EventId, holdId, from.UserId, next, candidate.UserId, Ttl, ct);
                if (outcome.Status == WaitlistHoldStatus.Ok)
                {
                    candidate.Offer(next, outcome.ExpiresAt!.Value);
                    return;
                }

                if (outcome.Status != WaitlistHoldStatus.HasHold)
                {
                    return;
                }
            }
        }

        await store.ReleaseHoldAsync(from.EventId, holdId, entry.GaTierIds, ct);
    }

    /// <summary>
    /// The id a passed-on hold gets: derived from the old one, so a retry after a crash between Redis and the save finds the hold it
    /// already passed and finishes that offer instead of leaving it with nobody (it is never swept).
    /// </summary>
    private static Guid PassedOn(Guid holdId)
    {
        Span<byte> bytes = stackalloc byte[16];
        holdId.TryWriteBytes(bytes);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task RecoverPassAsync(WaitlistEntry from, Guid next, CancellationToken ct)
    {
        if (await store.GetHoldAsync(from.EventId, next, ct) is not { } passed)
        {
            return;
        }

        var taker = await db.WaitlistEntries.FirstOrDefaultAsync(
            e => e.EventId == from.EventId && e.UserId == passed.UserId && e.Status == WaitlistStatus.Waiting, ct);
        taker?.Offer(next, passed.ExpiresAt);
    }

    private Task<List<WaitlistEntry>> CandidatesAsync(Guid eventId, Guid tierId, int units, Guid? except, CancellationToken ct) =>
        db.WaitlistEntries
            .Where(e => e.EventId == eventId && e.Status == WaitlistStatus.Waiting && (e.TierId == null || e.TierId == tierId)
                && e.Quantity <= units && e.Id != except)
            .OrderBy(e => e.QueuedAt)
            .ThenBy(e => e.Id)
            .Take(CandidatesPerTicket)
            .ToListAsync(ct);

    private Task<bool> HasOpenOrderAsync(Guid holdId, CancellationToken ct) =>
        db.Orders.AnyAsync(
            o => o.HoldId == holdId && (o.Status == OrderStatus.Pending || o.Status == OrderStatus.AwaitingPayment || o.Status == OrderStatus.PaymentFailed),
            ct);
}

/// <summary>A paid ticket was voided (spec 13 source 1): offer it to the waitlist.</summary>
public sealed class TicketReturnedWaitlistHandler(WaitlistOffers offers) : IIntegrationEventHandler<TicketReturned>
{
    public Task HandleAsync(TicketReturned message, IntegrationEventContext context, CancellationToken ct) => offers.OfferReturnedAsync(message, ct);
}

/// <summary>The waiter paid for the offered hold: the entry is done.</summary>
public sealed class OrderPaidWaitlistHandler(IAppDbContext db) : IIntegrationEventHandler<OrderPaid>
{
    public async Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct)
    {
        var entry = await db.WaitlistEntries.FirstOrDefaultAsync(
            e => e.EventId == message.EventId && e.UserId == message.UserId && e.Status == WaitlistStatus.Offered && e.OfferHoldId == message.HoldId, ct);
        entry?.Convert();
    }
}
