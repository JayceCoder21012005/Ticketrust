namespace TicketRush.Application.Abstractions.Inventory;

/// <summary>Seeds or reconciles an event's Redis inventory from the database.</summary>
public interface IInventorySeeder
{
    /// <returns>Null when the event is not published (nothing to seed).</returns>
    Task<ReconcileReport?> SeedAsync(Guid eventId, CancellationToken ct);
}
