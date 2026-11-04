using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Features.Catalog;
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

/// <summary><c>Draft/Published → Cancelled</c>; emits <see cref="EventCancelled"/> (refunds: spec 08).</summary>
public sealed class CancelEventHandler(IAppDbContext db, ICatalogCacheInvalidator cache)
{
    public async Task<Result<OrganizerEvent>> HandleAsync(Guid eventId, Guid organizerId, CancellationToken ct)
    {
        if (await OrganizerEvents.LoadOwnedAsync(db, eventId, organizerId, ct) is not { } evt)
        {
            return EventErrors.NotFound;
        }

        evt.Cancel();
        await db.SaveChangesAsync(ct);
        await cache.InvalidateEventAsync(evt.Id, evt.Slug, ct);
        return await OrganizerEvents.ToModelAsync(db, evt, ct);
    }
}
