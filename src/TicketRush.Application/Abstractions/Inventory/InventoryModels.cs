using TicketRush.Domain.Events;

namespace TicketRush.Application.Abstractions.Inventory;

public sealed record GaLine(Guid TierId, int Quantity);

/// <param name="SeatIndexes">Any order; the store sorts them before calling Redis (C9).</param>
/// <param name="EventGaTiers">Every GA tier of the event, so scripts receive all their keys up front.</param>
/// <param name="MaxTickets">Per-user limit of the phase the admission belongs to.</param>
public sealed record HoldRequest(
    Guid EventId,
    Guid UserId,
    IReadOnlyList<int> SeatIndexes,
    IReadOnlyList<GaLine> Ga,
    IReadOnlyList<Guid> EventGaTiers,
    int MaxTickets,
    bool ReplaceExisting,
    DateTimeOffset Now,
    TimeSpan Ttl);

public enum HoldStatus
{
    Ok,
    SeatUnavailable,
    GaInsufficient,
    LimitExceeded,
    HasHold,
}

public sealed record HoldOutcome(HoldStatus Status, Guid? HoldId, DateTimeOffset? ExpiresAt, IReadOnlyList<int> Unavailable, Guid? Tier)
{
    public static HoldOutcome Failed(HoldStatus status, IReadOnlyList<int>? unavailable = null, Guid? tier = null) =>
        new(status, null, null, unavailable ?? [], tier);
}

public enum CommitStatus
{
    Committed,

    /// <summary>The order was already committed (redelivery); nothing changed.</summary>
    Duplicate,
}

/// <param name="Version">The event's inventory version after the commit (unchanged for a duplicate).</param>
public sealed record CommitOutcome(CommitStatus Status, long Version);

public sealed record ActiveHold(Guid HoldId, Guid UserId, IReadOnlyList<int> Seats, IReadOnlyList<GaLine> Ga, DateTimeOffset ExpiresAt);

/// <param name="Warm">False until the event's inventory has been seeded from the database.</param>
public sealed record InventorySnapshot(long Version, IReadOnlyList<int> Held, IReadOnlyList<int> Sold, IReadOnlyDictionary<Guid, int> GaRemaining, bool Warm);

/// <summary>What the database says about an event's inventory; the reconciler makes Redis agree with it.</summary>
public sealed record InventorySeed(
    Guid EventId,
    int SeatCount,
    IReadOnlyDictionary<Guid, int> GaCapacity,
    IReadOnlyList<int> SoldSeats,
    IReadOnlyDictionary<Guid, int> GaSold,
    IReadOnlyDictionary<Guid, int> BoughtByUser,
    long? CommitFence = null);

public sealed record GaFix(int Before, int After);

public sealed record ReconcileReport(int SoldAdded, int SoldRemoved, IReadOnlyDictionary<Guid, GaFix> GaFixed)
{
    public bool HadDrift => SoldAdded > 0 || SoldRemoved > 0 || GaFixed.Count > 0;

    /// <summary>A checkout committed after the database was read; nothing was written and the seed must be read again.</summary>
    public bool Stale { get; init; }
}

/// <param name="SeatCount">Seats of the layout (from the seeded meta).</param>
public sealed record InventoryCounts(bool Warm, int SeatCount, int SeatsHeld, int SeatsSold, int GaRemaining, int GaHeld)
{
    /// <summary>Seats and GA tickets nobody holds or bought.</summary>
    public int Available => Math.Max(0, SeatCount - SeatsHeld - SeatsSold) + GaRemaining;

    public int Held => SeatsHeld + GaHeld;
}

public sealed record CatalogSeat(int Index, string Section, string Row, string Number, decimal X, decimal Y, Guid TierId);

/// <param name="Price">Whole VND, frozen at publish (spec 05), so the price shown is the price paid.</param>
public sealed record CatalogTier(Guid Id, string Name, long Price, TicketKind Kind, int? Capacity);

public sealed record CatalogPhase(Guid Id, DateTimeOffset OpensAt, DateTimeOffset ClosesAt, int MaxTicketsPerUser);

/// <summary>The static, published shape of an event that holds are checked and priced against.</summary>
public sealed record HoldCatalogEntry(
    Guid EventId,
    string Slug,
    IReadOnlyList<CatalogSeat> Seats,
    IReadOnlyList<CatalogTier> Tiers,
    IReadOnlyList<CatalogPhase> Phases)
{
    public IReadOnlyList<Guid> GaTierIds { get; } = [.. Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id)];

    public IReadOnlyDictionary<Guid, int> GaCapacity { get; } =
        Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).ToDictionary(t => t.Id, t => t.Capacity ?? 0);
}

/// <summary>Redis could not be reached. Callers fail closed (503 <c>system.busy</c>); nothing falls back to Postgres.</summary>
public sealed class InventoryUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
