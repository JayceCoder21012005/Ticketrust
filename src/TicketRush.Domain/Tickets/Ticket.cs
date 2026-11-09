using System.Security.Cryptography;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tickets;

/// <summary>
/// One admission, issued when an order is paid (spec 08; the ticket UI is spec 10).
/// Transitions: <c>Valid → CheckedIn</c>, <c>Valid ↔ Listed</c>, <c>Valid/Listed → Voided</c>, and
/// <c>Valid/Listed/CheckedIn → Voided</c> when the event is cancelled (<see cref="VoidForCancelledEvent"/>).
/// The DB's partial unique index on <c>(event_id, seat_index)</c> is the final guard against double-selling a seat.
/// </summary>
public sealed class Ticket : AggregateRoot, IAuditable
{
    public const int QrSecretLength = 32;

    private Ticket(Guid orderId, Guid eventId, Guid tierId, int? seatIndex, Guid ownerUserId)
        : base(Guid.CreateVersion7())
    {
        OrderId = orderId;
        EventId = eventId;
        TierId = tierId;
        SeatIndex = seatIndex;
        OwnerUserId = ownerUserId;
        Status = TicketStatus.Valid;
        QrSecret = RandomNumberGenerator.GetBytes(QrSecretLength);
    }

    // Required by EF Core.
    private Ticket()
    {
        QrSecret = [];
    }

    public Guid OrderId { get; private set; }

    public Guid EventId { get; private set; }

    public Guid TierId { get; private set; }

    /// <summary>Null for general admission.</summary>
    public int? SeatIndex { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public TicketStatus Status { get; private set; }

    /// <summary>
    /// 32 random bytes the QR code is derived from. The domain only holds the raw bytes: encrypting them at rest
    /// (ASP.NET Data Protection, spec 08) is the persistence layer's concern, e.g. a value converter in the EF mapping.
    /// </summary>
#pragma warning disable CA1819 // Raw secret bytes are exactly what EF maps and the QR signer consumes.
    public byte[] QrSecret { get; private set; }
#pragma warning restore CA1819

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Ticket Issue(Guid orderId, Guid eventId, Guid tierId, int? seatIndex, Guid ownerUserId)
    {
        if (orderId == Guid.Empty || eventId == Guid.Empty || tierId == Guid.Empty || ownerUserId == Guid.Empty)
        {
            throw new DomainException("ticket.invalid_reference", "A ticket needs an order, an event, a tier and an owner.");
        }

        if (seatIndex is < 0)
        {
            throw new DomainException("ticket.invalid_seat", "A seat index cannot be negative.");
        }

        return new Ticket(orderId, eventId, tierId, seatIndex, ownerUserId);
    }

    /// <summary>Valid → CheckedIn.</summary>
    public void CheckIn() => Move(TicketStatus.CheckedIn, nameof(CheckIn), TicketStatus.Valid);

    /// <summary>Valid → Listed (put up for resale).</summary>
    public void List() => Move(TicketStatus.Listed, nameof(List), TicketStatus.Valid);

    /// <summary>Listed → Valid (listing withdrawn).</summary>
    public void Unlist() => Move(TicketStatus.Valid, nameof(Unlist), TicketStatus.Listed);

    /// <summary>Valid or Listed → Voided, e.g. the event was cancelled. A checked-in or already voided ticket can't be voided.</summary>
    public void Void() => Move(TicketStatus.Voided, nameof(Void), TicketStatus.Valid, TicketStatus.Listed);

    /// <summary>
    /// Valid, Listed or CheckedIn → Voided because the organizer cancelled the event (spec 08): the whole order is refunded,
    /// so no ticket of it may stay usable, a checked-in one included. An already voided ticket is refused like everywhere else.
    /// </summary>
    public void VoidForCancelledEvent() =>
        Move(TicketStatus.Voided, nameof(VoidForCancelledEvent), TicketStatus.Valid, TicketStatus.Listed, TicketStatus.CheckedIn);

    private void Move(TicketStatus to, string method, params TicketStatus[] from)
    {
        if (!from.Contains(Status))
        {
            throw new DomainException("ticket.illegal_transition", $"Cannot {method} a ticket that is {Status}.");
        }

        Status = to;
    }
}
