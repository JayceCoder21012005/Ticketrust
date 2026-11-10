namespace TicketRush.Domain.Orders;

/// <summary>Snapshot of who bought, taken at order creation so later profile edits don't rewrite history.</summary>
public sealed record BuyerInfo(string Name, string Email, string? Phone);
