using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Builds an event's inventory seed from the database and hands it to the store (warm on publish, reconcile).</summary>
public sealed class InventorySeeder(IHoldCatalog catalog, ISoldInventorySource sold, IInventoryStore store) : IInventorySeeder
{
    private const int MaxAttempts = 3;

    /// <returns>Null when the event is not published (nothing to warm).</returns>
    public async Task<ReconcileReport?> SeedAsync(Guid eventId, CancellationToken ct)
    {
        if (await catalog.GetAsync(eventId, ct) is not { } entry)
        {
            return null;
        }

        // Fence: a checkout that commits after the database read is missing from it, so the script refuses (Stale) and
        // the seed is read again rather than undoing that commit.
        ReconcileReport? report = null;
        for (var attempt = 0; attempt < MaxAttempts && report is not { Stale: false }; attempt++)
        {
            var fence = await store.GetCommitFenceAsync(eventId, ct);
            var soldNow = await sold.GetAsync(eventId, ct);
            report = await store.ReconcileAsync(
                new InventorySeed(eventId, entry.Seats.Count, entry.GaCapacity, soldNow.Seats, soldNow.GaSold, soldNow.BoughtByUser, fence),
                ct);
        }

        return report;
    }
}

/// <summary>Until spec 08 creates tickets nothing is sold in the database; 08 replaces this source.</summary>
public sealed class NoTicketsSoldInventorySource : ISoldInventorySource
{
    private static readonly SoldInventory Empty = new([], new Dictionary<Guid, int>(), new Dictionary<Guid, int>());

    public Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct) => Task.FromResult(Empty);
}

/// <summary>Warms the inventory as soon as an event is published (spec 07), well before its first phase opens.</summary>
public sealed class InventoryWarmHandler(InventorySeeder seeder) : IIntegrationEventHandler<EventPublished>
{
    public Task HandleAsync(EventPublished message, IntegrationEventContext context, CancellationToken ct) => seeder.SeedAsync(message.EventId, ct);
}
