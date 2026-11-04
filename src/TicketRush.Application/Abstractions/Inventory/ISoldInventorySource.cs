namespace TicketRush.Application.Abstractions.Inventory;

/// <param name="Seats">Indexes of sold seats.</param>
/// <param name="GaSold">GA tickets sold per tier.</param>
/// <param name="BoughtByUser">Tickets each user bought for the event (per-user limit).</param>
public sealed record SoldInventory(IReadOnlyList<int> Seats, IReadOnlyDictionary<Guid, int> GaSold, IReadOnlyDictionary<Guid, int> BoughtByUser);

/// <summary>What the database records as sold. The tickets table arrives with spec 08, which replaces the empty source.</summary>
public interface ISoldInventorySource
{
    Task<SoldInventory> GetAsync(Guid eventId, CancellationToken ct);
}

/// <summary>Events whose inventory the workers keep warm, swept and reconciled: a phase open now or opening soon.</summary>
public sealed record LiveInventoryEvent(Guid EventId, IReadOnlyList<Guid> GaTierIds);

public interface IInventoryEventSource
{
    Task<IReadOnlyList<LiveInventoryEvent>> ListLiveAsync(DateTimeOffset now, TimeSpan lookahead, CancellationToken ct);
}
