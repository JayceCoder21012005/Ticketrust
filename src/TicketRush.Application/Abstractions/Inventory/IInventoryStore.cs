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
}

/// <summary>Published events only; null for drafts, cancelled and unknown events.</summary>
public interface IHoldCatalog
{
    Task<HoldCatalogEntry?> GetAsync(Guid eventId, CancellationToken ct);
}
