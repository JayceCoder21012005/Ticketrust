namespace TicketRush.Application.Abstractions.Inventory;

/// <summary>
/// The database's final word on general-admission stock (spec 08, C1). Redis hands out holds; this is the conditional
/// <c>sold_count</c> update that decides which payments really get tickets.
/// </summary>
public interface ITierInventory
{
    /// <summary>
    /// Atomically adds <paramref name="quantity"/> to the tier's sold count when that stays within its capacity.
    /// Runs on the caller's current transaction, so rolling it back releases the claim.
    /// </summary>
    /// <returns><c>false</c> (nothing changed) when the tier is unknown, is not general admission, or would oversell.</returns>
    Task<bool> TryClaimAsync(Guid tierId, int quantity, CancellationToken ct);

    /// <summary>
    /// Gives <paramref name="quantity"/> claimed units back (<c>sold_count = sold_count - q</c>), never going below zero.
    /// Invariant: <c>sold_count</c> must equal the number of non-voided general-admission tickets of the tier, so every GA
    /// ticket that is voided or refunded must be released <b>in the same transaction</b> as the void (B8/B9); otherwise the
    /// claim guard drifts from what <c>DbSoldInventorySource</c> feeds into Redis. Same connection and transaction
    /// semantics as <see cref="TryClaimAsync"/>.
    /// </summary>
    /// <returns><c>false</c> (nothing changed) when the tier is unknown or fewer than <paramref name="quantity"/> are claimed.</returns>
    Task<bool> ReleaseAsync(Guid tierId, int quantity, CancellationToken ct);
}
