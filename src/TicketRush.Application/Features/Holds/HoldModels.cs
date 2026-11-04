using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Holds;

public sealed record BestAvailableRequest(Guid TierId, int Quantity);

/// <param name="PhaseId">From the validated admission token: the phase whose per-user limit applies.</param>
public sealed record CreateHoldCommand(
    Guid UserId,
    Guid EventId,
    Guid PhaseId,
    IReadOnlyList<int>? SeatIndexes,
    IReadOnlyList<GaLine>? Ga,
    BestAvailableRequest? BestAvailable,
    bool ReplaceExisting);

/// <param name="Price">Whole VND, from the frozen tier: the price shown is the price paid.</param>
public sealed record HeldSeat(int Index, string Section, string Row, string Number, Guid TierId, long Price);

public sealed record HeldGa(Guid TierId, string Name, int Quantity, long Price);

public sealed record HoldResponse(
    Guid HoldId,
    IReadOnlyList<HeldSeat> Seats,
    IReadOnlyList<HeldGa> Ga,
    long Total,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerNow);

public static class HoldErrors
{
    public static readonly Error InsufficientQuantityAny =
        Error.Conflict("hold.insufficient_quantity", "Not enough tickets are left for this selection.");

    public static readonly Error LimitExceeded =
        Error.Conflict("hold.limit_exceeded", "This selection is over the ticket limit per buyer.");

    public static readonly Error AlreadyExists =
        Error.Conflict("hold.already_exists", "You already hold tickets for this event. Replace them or release them first.");

    public static readonly Error EmptySelection = Error.Validation("hold.empty_selection", "Choose at least one seat or ticket.");

    public static readonly Error InvalidSeat = Error.Validation("hold.invalid_seat", "That seat or ticket type does not belong to this event.");

    public static readonly Error NotFound = Error.NotFound("hold.not_found", "You are not holding any tickets for this event.");

    public static Error SeatsUnavailable(IReadOnlyList<int> seats) =>
        new("hold.seats_unavailable", "Some of these seats were just taken.", ErrorType.Conflict, Extensions: new Dictionary<string, object?> { ["unavailable"] = seats });

    public static Error InsufficientQuantity(Guid tierId) =>
        InsufficientQuantityAny with { Extensions = new Dictionary<string, object?> { ["tierId"] = tierId } };
}

internal static class HoldPricing
{
    public static HoldResponse ToResponse(HoldCatalogEntry catalog, Guid holdId, IReadOnlyList<int> seats, IReadOnlyList<GaLine> ga, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var tiers = catalog.Tiers.ToDictionary(t => t.Id);
        var byIndex = catalog.Seats.ToDictionary(s => s.Index);
        var heldSeats = seats.Order()
            .Select(i => byIndex[i])
            .Select(s => new HeldSeat(s.Index, s.Section, s.Row, s.Number, s.TierId, tiers[s.TierId].Price))
            .ToList();
        var heldGa = ga.Where(l => l.Quantity > 0)
            .Select(l => new HeldGa(l.TierId, tiers[l.TierId].Name, l.Quantity, tiers[l.TierId].Price))
            .ToList();
        var total = heldSeats.Sum(s => s.Price) + heldGa.Sum(g => g.Price * g.Quantity);
        return new HoldResponse(holdId, heldSeats, heldGa, total, expiresAt, now);
    }
}
