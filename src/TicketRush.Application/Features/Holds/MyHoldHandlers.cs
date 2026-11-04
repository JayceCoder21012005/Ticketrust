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

/// <summary><c>DELETE /events/{id}/holds/me</c>: idempotent; succeeds when there is nothing (left) to release.</summary>
public sealed class ReleaseMyHoldHandler(IHoldCatalog catalog, IInventoryStore store)
{
    public async Task<Result> HandleAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        if (await catalog.GetAsync(eventId, ct) is not { } entry)
        {
            return EventErrors.NotFound;
        }

        try
        {
            if (await store.GetUserHoldAsync(eventId, userId, ct) is { } hold)
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
