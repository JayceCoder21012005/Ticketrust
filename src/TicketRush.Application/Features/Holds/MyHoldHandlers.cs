using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Holds;

/// <summary><c>GET /events/{id}/holds/me</c>: the buyer's live hold, priced, or 404 <c>hold.not_found</c>.</summary>
public sealed class GetMyHoldHandler(IHoldCatalog catalog, IInventoryStore store, TimeProvider time)
{
    public async Task<Result<HoldResponse>> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        if (await catalog.GetAsync(eventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        try
        {
            return await store.GetUserHoldAsync(eventId, userId, ct) is { } hold
                ? HoldPricing.ToResponse(entry, hold.HoldId, hold.Seats, hold.Ga, hold.ExpiresAt, time.GetUtcNow())
                : HoldErrors.NotFound;
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }
}

/// <summary>
/// <c>DELETE /events/{id}/holds/me</c>: idempotent; succeeds when there is nothing (left) to release. Cancelling an order
/// (spec 08) reuses it with <c>onlyHoldId</c>, so a newer hold the buyer made since is never released by accident.
/// A waitlist offer's hold is declined instead (spec 13): it goes to the next in line, never to the public sale first.
/// </summary>
public sealed class ReleaseMyHoldHandler(IHoldCatalog catalog, IInventoryStore store, Waitlist.IWaitlistOfferDecliner? waitlist = null)
{
    public Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct) => HandleAsync(userId, eventId, null, ct);

    /// <param name="onlyHoldId">When set, the buyer's current hold is released only if it is this one.</param>
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, Guid? onlyHoldId, CancellationToken ct)
    {
        if (await catalog.GetAsync(eventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        try
        {
            if (await store.GetUserHoldAsync(eventId, userId, ct) is { } hold && (onlyHoldId is null || hold.HoldId == onlyHoldId)
                && (waitlist is null || !await waitlist.DeclineAsync(eventId, userId, hold.HoldId, ct)))
            {
                await store.ReleaseHoldAsync(eventId, hold.HoldId, entry.GaTierIds, ct);
            }

            return Result.Success();
        }
        catch (InventoryUnavailableException)
        {
            return CommonErrors.SystemBusy;
        }
    }
}
