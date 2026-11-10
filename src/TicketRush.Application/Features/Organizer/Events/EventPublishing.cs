using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Catalog;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Publishing;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Organizer.Events;

public sealed record PublishChecklistResult(bool Ready, IReadOnlyList<PublishFailure> Failures);

public sealed class GetPublishChecklistHandler(IAppDbContext db, TimeProvider time)
{
    public async Task<Result<PublishChecklistResult>> HandleAsync(Guid eventId, Guid organizerId, CancellationToken ct)
    {
        if (await OrganizerEvents.LoadOwnedAsync(db, eventId, organizerId, ct) is not { } evt)
        {
            return EventErrors.NotFound;
        }

        // The checklist gates Draft → Published only. After that the inventory is locked, so a rule that stops holding
        // (sales opened, say) is not something the organizer could or should fix.
        if (evt.Status != EventStatus.Draft)
        {
            return new PublishChecklistResult(true, []);
        }

        var failures = await RunAsync(db, evt, time, ct);
        return new PublishChecklistResult(failures.Count == 0, failures);
    }

    internal static async Task<IReadOnlyList<PublishFailure>> RunAsync(IAppDbContext db, Event evt, TimeProvider time, CancellationToken ct) =>
        PublishChecklist.Run(new PublishContext(evt, await OrganizerEvents.SeatsPerTierAsync(db, evt.Id, ct), time.GetUtcNow()));
}

/// <summary>Either the published event, or the checklist rules that failed (nothing changed).</summary>
public sealed record PublishOutcome(OrganizerEvent? Event, IReadOnlyList<PublishFailure> Failures);

/// <summary>
/// Spec 05: checklist, then <c>Draft → Published</c> with one <see cref="EventPublished"/> in the outbox. Idempotent, and
/// two racing publishes can't both write a message: the xmin token makes the loser reload and find it published.
/// </summary>
public sealed class PublishEventHandler(IAppDbContext db, ICatalogCacheInvalidator cache, TimeProvider time)
{
    private const int Attempts = 2;

    public async Task<Result<PublishOutcome>> HandleAsync(Guid eventId, Guid organizerId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (await OrganizerEvents.LoadOwnedAsync(db, eventId, organizerId, ct) is not { } evt)
            {
                return EventErrors.NotFound;
            }

            if (evt.Status == EventStatus.Published)
            {
                return new PublishOutcome(await OrganizerEvents.ToModelAsync(db, evt, ct), []);
            }

            if (evt.Status == EventStatus.Draft
                && await GetPublishChecklistHandler.RunAsync(db, evt, time, ct) is { Count: > 0 } failures)
            {
                return new PublishOutcome(null, failures);
            }

            evt.Publish();
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < Attempts)
            {
                // Someone else saved first (often another publish). Drop our unit of work, outbox row included, and look again.
                db.ChangeTracker.Clear();
                continue;
            }

            await cache.InvalidateEventAsync(evt.Id, evt.Slug, ct);
            return new PublishOutcome(await OrganizerEvents.ToModelAsync(db, evt, ct), []);
        }
    }
}

/// <summary>
/// <c>Draft/Published → Cancelled</c>; emits <see cref="EventCancelled"/>, whose consumer settles the orders (spec 08, B9).
/// The status change commits in a transaction that first takes the event sale lock <b>exclusively</b>
/// (<see cref="IEventSaleLock.AcquireExclusiveAsync"/>, bounded by <see cref="FulfilmentOptions.LockTimeout"/>): a
/// fulfilment in flight holds the shared lock, so it commits its order Paid before the cancel does (and the settlement then
/// sends it to refund); one that starts later waits for the cancel and reads Cancelled (refund <c>event_cancelled</c>, no
/// tickets). Ownership is checked before the lock, so nobody else's request can make the event's buyers wait. A wait longer
/// than the lock timeout rolls back and answers 409 <c>event.cancel_busy</c> (try again), not a 5xx.
/// </summary>
public sealed class CancelEventHandler(IAppDbContext db, IEventSaleLock saleLock, ICatalogCacheInvalidator cache, IOptions<FulfilmentOptions> options)
{
    public async Task<Result<OrganizerEvent>> HandleAsync(Guid eventId, Guid organizerId, CancellationToken ct)
    {
        Event? evt;
        try
        {
            evt = await db.InTransactionAsync(token => CancelLockedAsync(eventId, organizerId, token), options.Value.LockTimeout, ct);
        }
        catch (DatabaseLockTimeoutException)
        {
            // Payments of this event were being confirmed for longer than the lock timeout: nothing changed, try again.
            return EventErrors.CancelBusy;
        }

        if (evt is null)
        {
            return EventErrors.NotFound;
        }

        await cache.InvalidateEventAsync(evt.Id, evt.Slug, ct);
        return await OrganizerEvents.ToModelAsync(db, evt, ct);
    }

    private async Task<Event?> CancelLockedAsync(Guid eventId, Guid organizerId, CancellationToken ct)
    {
        if (await OrganizerEvents.LoadOwnedAsync(db, eventId, organizerId, ct) is not { } evt)
        {
            return null;
        }

        // The status was read before the lock; the event's xmin makes the save fail if anyone changed it meanwhile.
        await saleLock.AcquireExclusiveAsync(eventId, ct);
        evt.Cancel();
        await db.SaveChangesAsync(ct);
        return evt;
    }
}
