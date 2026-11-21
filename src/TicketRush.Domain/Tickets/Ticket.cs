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

    /// <summary>
    /// The statuses <see cref="VoidForCancelledEvent"/> accepts. The cancelled-event settlement voids tickets with one
    /// set-based UPDATE (it must not load <see cref="QrSecret"/>, which needs the Data Protection key ring) and filters on
    /// this list, so the rule lives here once.
    /// </summary>
    public static IReadOnlyList<TicketStatus> StatusesVoidableForCancelledEvent { get; } =
        [TicketStatus.Valid, TicketStatus.Listed, TicketStatus.CheckedIn];

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

    /// <summary>When the gate admitted this ticket (spec 11). Null until then; kept if the ticket is voided afterwards.</summary>
    public DateTimeOffset? CheckedInAt { get; private set; }

    /// <summary>The gate staff user who admitted it (spec 11).</summary>
    public Guid? CheckedInByUserId { get; private set; }

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

    /// <summary>
    /// Valid → CheckedIn, recording who admitted it and when. The gate itself admits with one conditional UPDATE (spec 11)
    /// so two scans cannot both win; this method is the same rule for code that already holds the aggregate.
    /// </summary>
    public void CheckIn(Guid staffUserId, DateTimeOffset at)
    {
        if (staffUserId == Guid.Empty)
        {
            throw new DomainException("ticket.invalid_reference", "A check-in needs the staff user who scanned the ticket.");
        }

        Move(TicketStatus.CheckedIn, nameof(CheckIn), TicketStatus.Valid);
        CheckedInAt = at;
        CheckedInByUserId = staffUserId;
    }

    /// <summary>Valid → Listed (put up for resale).</summary>
    public void List() => Move(TicketStatus.Listed, nameof(List), TicketStatus.Valid);

    /// <summary>Listed → Valid (listing withdrawn).</summary>
    public void Unlist() => Move(TicketStatus.Valid, nameof(Unlist), TicketStatus.Listed);

    /// <summary>
    /// Valid or Listed → Voided while the event is on (e.g. a refund approved by an admin, spec 16): the ticket goes back to the sale,
    /// first to the waitlist (spec 13, <see cref="TicketReturned"/>). A checked-in or already voided ticket can't be voided.
    /// </summary>
    public void Void()
    {
        Move(TicketStatus.Voided, nameof(Void), TicketStatus.Valid, TicketStatus.Listed);
        Raise(new TicketReturned(Id, EventId, TierId, SeatIndex, OwnerUserId));
    }

    /// <summary>
    /// Listed → Voided because the ticket changes owner (spec 14: resale paid or transfer accepted). Unlike <see cref="Void"/>
    /// nothing goes back to the sale: the seat stays sold and <see cref="Reissue"/> gives it to the new owner.
    /// </summary>
    public void HandOver() => Move(TicketStatus.Voided, nameof(HandOver), TicketStatus.Listed);

    /// <summary>
    /// The new owner's ticket for the same seat (or GA unit) of a ticket just <see cref="HandOver">handed over</see>, with a
    /// new <see cref="QrSecret"/> so the old QR dies. Insert it after the void is saved: the partial unique seat index counts
    /// only non-voided rows.
    /// </summary>
    public Ticket Reissue(Guid orderId, Guid ownerUserId)
    {
        if (Status != TicketStatus.Voided)
        {
            throw new DomainException("ticket.illegal_transition", $"Cannot reissue a ticket that is {Status}; hand it over first.");
        }

        return Issue(orderId, EventId, TierId, SeatIndex, ownerUserId);
    }

    /// <summary>
    /// Valid, Listed or CheckedIn → Voided because the organizer cancelled the event (spec 08): the whole order is refunded,
    /// so no ticket of it may stay usable, a checked-in one included. An already voided ticket is refused like everywhere else.
    /// </summary>
    public void VoidForCancelledEvent() =>
        Move(TicketStatus.Voided, nameof(VoidForCancelledEvent), [.. StatusesVoidableForCancelledEvent]);

    private void Move(TicketStatus to, string method, params TicketStatus[] from)
    {
        if (!from.Contains(Status))
        {
            throw new DomainException("ticket.illegal_transition", $"Cannot {method} a ticket that is {Status}.");
        }

        Status = to;
    }
}
