namespace TicketRush.Application.Abstractions.Resale;

/// <summary>
/// The Redis half of a resale reservation (spec 14): <c>SET resale:{evt}:listing:&lt;id&gt; &lt;buyerId&gt; NX PX 600000</c>. The
/// database row (<c>status = 'Reserved'</c>) is the other half; both must succeed. Redis being unreachable throws
/// <see cref="Inventory.InventoryUnavailableException"/>.
/// </summary>
public interface IResaleReservations
{
    /// <returns>True when the key is now the buyer's: newly set, or already theirs (a retry).</returns>
    Task<bool> TryReserveAsync(Guid eventId, Guid listingId, Guid buyerId, TimeSpan ttl, CancellationToken ct);

    /// <summary>Deletes the key only while it is still <paramref name="buyerId"/>'s.</summary>
    Task ReleaseAsync(Guid eventId, Guid listingId, Guid buyerId, CancellationToken ct);
}
