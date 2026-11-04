using System.Buffers.Text;
using System.Text.Json;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Orders;

/// <param name="Name">Required, up to 100 characters.</param>
/// <param name="Email">Required, valid address.</param>
/// <param name="Phone">Optional Vietnamese number (<c>0xxxxxxxxx</c> or <c>+84xxxxxxxxx</c>).</param>
public sealed record BuyerInput(string Name, string Email, string? Phone);

/// <param name="HoldId">Must be the buyer's live hold for <paramref name="EventId"/>.</param>
public sealed record CreateOrderCommand(Guid UserId, Guid EventId, Guid HoldId, BuyerInput Buyer);

/// <summary>A seated line; labels come from the event layout, the price is the one copied from the hold.</summary>
public sealed record OrderSeat(int Index, string Section, string Row, string Number, Guid TierId, long Price);

public sealed record OrderGaLine(Guid TierId, string Name, int Quantity, long Price);

public sealed record OrderBuyer(string Name, string Email, string? Phone);

/// <param name="Total">Whole VND, the sum of the lines (copied from the hold, never recomputed).</param>
/// <param name="ExpiresAt">The hold's expiry: the order cannot be paid after it.</param>
/// <param name="ServerNow">Clock reference for the countdown (same idea as <c>HoldResponse.ServerNow</c>).</param>
/// <param name="RefundReason">Set once the order needs a refund: <c>seats_unavailable</c> or <c>event_cancelled</c>.</param>
/// <param name="TicketCount">Tickets issued; only present while the order is <see cref="OrderStatus.Paid"/>.</param>
public sealed record OrderResponse(
    Guid Id,
    Guid EventId,
    OrderStatus Status,
    IReadOnlyList<OrderSeat> Seats,
    IReadOnlyList<OrderGaLine> Ga,
    long Total,
    OrderBuyer Buyer,
    DateTimeOffset ExpiresAt,
    DateTimeOffset ServerNow,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    string? RefundReason,
    int? TicketCount);

/// <summary>What <c>POST /orders</c> answers with: <paramref name="Created"/> is false when the hold already had an open order.</summary>
public sealed record CreatedOrder(OrderResponse Order, bool Created);

public sealed record OrderSummary(
    Guid Id,
    Guid EventId,
    string EventTitle,
    string EventSlug,
    DateTimeOffset EventStartsAt,
    OrderStatus Status,
    long Total,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

public sealed record OrderPage(IReadOnlyList<OrderSummary> Items, string? NextCursor);

public sealed record ListMyOrdersQuery(Guid UserId, string? Cursor, int Limit = ListMyOrdersQuery.DefaultLimit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
}

/// <summary>Keyset position of "my orders": the last item's (CreatedAt, Id). Opaque to clients (base64url JSON).</summary>
public sealed record OrderCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public static string Encode(DateTimeOffset createdAt, Guid id) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new OrderCursor(createdAt, id)));

    public static bool TryDecode(string? value, out OrderCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || !Base64Url.IsValid(value))
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<OrderCursor>(Base64Url.DecodeFromChars(value));
            if (decoded is null || decoded.Id == Guid.Empty || decoded.CreatedAt == default)
            {
                return false;
            }

            // Npgsql only writes UTC offsets to timestamptz; a hand-made cursor may carry any offset.
            cursor = decoded with { CreatedAt = decoded.CreatedAt.ToUniversalTime() };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public static class OrderErrors
{
    /// <summary>Also returned for another buyer's order, so ids cannot be probed.</summary>
    public static readonly Error NotFound = Error.NotFound("order.not_found", "Order not found.");

    /// <summary>The buyer holds nothing for this event, or <c>holdId</c> is not the hold they hold.</summary>
    public static readonly Error HoldInvalid =
        Error.Conflict("order.hold_invalid", "You are not holding these tickets. Choose your seats again.");

    /// <summary>The hold is gone (expired or released) before the order could be made.</summary>
    public static readonly Error HoldExpired =
        Error.Conflict("order.hold_expired", "Your hold has expired. Choose your seats again.");

    /// <summary>The order is not in a state that allows the action (cancel after payment started, pay after it ended).</summary>
    public static readonly Error NotPayable =
        Error.Conflict("order.not_payable", "This order can no longer be changed this way.");

    /// <summary>The order's time ran out (used when paying; see spec 08).</summary>
    public static readonly Error Expired = Error.Conflict("order.expired", "This order has expired.");
}
