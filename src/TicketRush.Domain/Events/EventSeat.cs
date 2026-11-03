using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Events;

/// <summary>
/// One seat of an event's static layout. <see cref="Index"/> (0..n-1) is the compact id used in every realtime
/// and Redis payload (spec 07). Held/sold state is never stored here: it lives in Redis and in tickets.
/// </summary>
public sealed class EventSeat : Entity
{
    private EventSeat(Guid id, Guid eventId, int index, string section, string row, string number, decimal x, decimal y, Guid tierId)
        : base(id)
    {
        EventId = eventId;
        Index = index;
        Section = section;
        Row = row;
        Number = number;
        X = x;
        Y = y;
        TierId = tierId;
    }

    private EventSeat()
    {
    }

    public Guid EventId { get; private set; }

    public int Index { get; private set; }

    public string Section { get; private set; } = null!;

    public string Row { get; private set; } = null!;

    public string Number { get; private set; } = null!;

    public decimal X { get; private set; }

    public decimal Y { get; private set; }

    public Guid TierId { get; private set; }

    public static EventSeat Create(Guid eventId, int index, string section, string row, string number, decimal x, decimal y, Guid tierId)
    {
        if (index < 0)
        {
            throw new DomainException("seat.invalid_index", "Seat index must not be negative.");
        }

        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(row) || string.IsNullOrWhiteSpace(number))
        {
            throw new DomainException("seat.invalid_label", "Seat section, row and number are required.");
        }

        return new EventSeat(Guid.CreateVersion7(), eventId, index, section.Trim(), row.Trim(), number.Trim(), x, y, tierId);
    }
}
