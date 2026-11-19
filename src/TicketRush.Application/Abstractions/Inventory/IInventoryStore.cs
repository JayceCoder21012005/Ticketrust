namespace TicketRush.Application.Abstractions.Inventory;

/// <summary>
/// Seat and GA inventory of events, in Redis (spec 07). Every mutation is one atomic script that also bumps the event's
/// version and publishes a seat delta. Throws <see cref="InventoryUnavailableException"/> when Redis is unreachable.
/// </summary>
public interface IInventoryStore
{
    Task<HoldOutcome> CreateHoldAsync(HoldRequest request, CancellationToken ct);

    /// <returns>False when the hold no longer exists (releasing is idempotent).</returns>
    Task<bool> ReleaseHoldAsync(Guid eventId, Guid holdId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);

    Task<ActiveHold?> GetUserHoldAsync(Guid eventId, Guid userId, CancellationToken ct);

    /// <summary>
    /// True while the hold's record still exists for this user: live, or expired and not yet swept. Lets checkout tell
    /// "your hold just expired" from "that is not your hold".
    /// </summary>
    Task<bool> HoldRecordedAsync(Guid eventId, Guid holdId, Guid userId, CancellationToken ct);

    /// <summary>A hold by id, live or expired but not yet swept (the waitlist passes expired offers on); null once it is gone.</summary>
    Task<ActiveHold?> GetHoldAsync(Guid eventId, Guid holdId, CancellationToken ct);

    /// <summary>
    /// Called by checkout (08) after the database commit; also covers a hold that already expired. Idempotent per
    /// <paramref name="orderId"/>: a redelivery answers <see cref="CommitStatus.Duplicate"/> and changes nothing.
    /// </summary>
    Task<CommitOutcome> CommitAsync(Guid eventId, Guid orderId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);

    Task<InventorySnapshot> GetSnapshotAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);

    /// <summary>Totals only (no seat lists): what the waiting room needs to tell AllHeld from SoldOut.</summary>
    Task<InventoryCounts> GetCountsAsync(Guid eventId, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);

    /// <summary>True once the event's inventory has been seeded (cheap: one field).</summary>
    Task<bool> IsWarmAsync(Guid eventId, CancellationToken ct);

    /// <summary>Holds of the event whose expiry is at or before <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<Guid>> DueHoldsAsync(Guid eventId, DateTimeOffset now, int limit, CancellationToken ct);

    /// <summary>Checkout commits counted so far; read it before the database and pass it in <see cref="InventorySeed.CommitFence"/>.</summary>
    Task<long> GetCommitFenceAsync(Guid eventId, CancellationToken ct);

    /// <summary>Makes Redis agree with the database (sold seats, GA remaining, bought counts) and marks the event warm.</summary>
    Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct);

    /// <summary>
    /// Spec 13: a voided ticket's seats / GA units become a waitlist hold of <paramref name="userId"/> in one step (never free in
    /// between); <paramref name="previousOwner"/> stops counting them. The hold is not swept on expiry: the waitlist expires it.
    /// </summary>
    Task<WaitlistHoldOutcome> HoldReturnedAsync(
        Guid eventId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, Guid previousOwner, TimeSpan ttl, CancellationToken ct);

    /// <summary>Spec 13: an expired waitlist hold moves, as it is, to the next waitlist holder.</summary>
    Task<WaitlistHoldOutcome> PassHoldAsync(Guid eventId, Guid oldHoldId, Guid oldUserId, Guid newHoldId, Guid newUserId, TimeSpan ttl, CancellationToken ct);

    /// <summary>Spec 13: nobody waits for a voided ticket: its seats / GA units go back to the public sale.</summary>
    Task FreeReturnedAsync(Guid eventId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, Guid previousOwner, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct);
}

public enum WaitlistHoldStatus
{
    Ok,

    /// <summary>The new holder already holds tickets of the event.</summary>
    HasHold,

    /// <summary>A returned seat is held by someone else.</summary>
    Taken,

    /// <summary>The waitlist hold to pass on no longer exists.</summary>
    NotFound,
}

public sealed record WaitlistHoldOutcome(WaitlistHoldStatus Status, DateTimeOffset? ExpiresAt);

/// <summary>Published events only; null for drafts, cancelled and unknown events.</summary>
public interface IHoldCatalog
{
    Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct);
}
