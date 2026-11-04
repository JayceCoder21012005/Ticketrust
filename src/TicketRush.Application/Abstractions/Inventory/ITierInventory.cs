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
}
