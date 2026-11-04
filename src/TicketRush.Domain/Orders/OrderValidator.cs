using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Orders;

/// <summary>Creation-time invariants of <see cref="Order"/>, kept apart so the aggregate stays readable.</summary>
internal static class OrderValidator
{
    public static void EnsureBuyer(BuyerInfo buyer)
    {
        if (string.IsNullOrWhiteSpace(buyer.Name) || string.IsNullOrWhiteSpace(buyer.Email))
        {
            throw new DomainException("order.invalid_buyer", "The buyer's name and email are required.");
        }
    }

    public static void EnsureItemsAndTotal(IReadOnlyList<OrderItem> items, Money total)
    {
        if (items.Count == 0)
        {
            throw new DomainException("order.no_items", "An order needs at least one item.");
        }

        foreach (var item in items)
        {
            EnsureItem(item);
        }

        if (SumOf(items, total.Currency) != total)
        {
            throw new DomainException("order.total_mismatch", "The total must equal the sum of unit price times quantity.");
        }
    }

    /// <summary>Overflow of unit price x quantity or of the sum can never equal a valid total, so it is a mismatch.</summary>
    private static Money? SumOf(IReadOnlyList<OrderItem> items, string currency)
    {
        try
        {
            var sum = Money.Zero(currency);
            foreach (var item in items)
            {
                sum += item.LineTotal();
            }

            return sum;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static void EnsureItem(OrderItem item)
    {
        var invalid = item.TierId == Guid.Empty
            || item.Quantity is < 1 or > Order.MaxQuantityPerItem
            || item.SeatIndex is < 0
            || (item.SeatIndex is not null && item.Quantity != 1);

        if (invalid)
        {
            throw new DomainException("order.invalid_item", $"Each item needs a tier and a quantity of 1 to {Order.MaxQuantityPerItem} (exactly 1 for a seat).");
        }
    }
}
