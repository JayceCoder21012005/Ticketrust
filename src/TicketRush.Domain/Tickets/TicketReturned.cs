using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tickets;

/// <summary>
/// A paid ticket was voided while its event is on (spec 13's <c>InventoryReturned</c>): its seat (or one GA unit of its tier) goes
/// back to the sale, first to the waitlist. <paramref name="PreviousOwnerUserId"/> stops counting it toward their purchase cap.
/// </summary>
public sealed record TicketReturned(Guid TicketId, Guid EventId, Guid TierId, int? SeatIndex, Guid PreviousOwnerUserId) : IDomainEvent;
