using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>
/// One line of an order, copied from the hold. A seated line is exactly one seat
/// (<see cref="SeatIndex"/> set, quantity 1); a general-admission line has no seat and a quantity.
/// <see cref="UnitPrice"/> is the price at hold time and is never recomputed from the live catalog.
/// </summary>
public sealed record OrderItem(Guid TierId, int? SeatIndex, Money UnitPrice, int Quantity)
{
    // Required by EF Core, which cannot bind the Money parameter of the primary constructor to a mapped column.
    private OrderItem()
        : this(Guid.Empty, null, Money.Zero(), 0)
    {
    }

    /// <summary>A method, not a property, so JSON/EF mappings of the record don't pick it up.</summary>
    public Money LineTotal() => UnitPrice * Quantity;
}
