using TicketRush.Application.Abstractions.Inventory;

namespace TicketRush.Infrastructure.Inventory;

/// <summary>Checkout commit and database reconciliation.</summary>
public sealed partial class RedisInventoryStore
{
    public Task CommitAsync(Guid eventId, Guid holdId, Guid userId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, IReadOnlyList<Guid> eventGaTiers, CancellationToken ct) =>
        GuardAsync(() => InventoryScripts.CommitAsync(redis.GetDatabase(), keys, eventId, holdId, userId, seats, ga, eventGaTiers));

    public Task<ReconcileReport> ReconcileAsync(InventorySeed seed, CancellationToken ct) =>
        throw new NotSupportedException("inventory_reconcile lands in plan task 5.");
}
