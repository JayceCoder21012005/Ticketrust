using TicketRush.Domain.Events;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Events;

public sealed class EventSeatTests
{
    [Fact]
    public void Create_trims_labels_and_keeps_the_compact_index()
    {
        var seat = EventSeat.Create(Guid.NewGuid(), 7, " A ", " 3 ", " 12 ", 10.5m, 20m, Guid.NewGuid());

        seat.Index.ShouldBe(7);
        (seat.Section, seat.Row, seat.Number).ShouldBe(("A", "3", "12"));
    }

    [Fact]
    public void Create_rejects_a_negative_index()
    {
        Should.Throw<DomainException>(() => EventSeat.Create(Guid.NewGuid(), -1, "A", "1", "1", 0, 0, Guid.NewGuid()))
            .Code.ShouldBe("seat.invalid_index");
    }

    [Fact]
    public void Create_rejects_blank_labels()
    {
        Should.Throw<DomainException>(() => EventSeat.Create(Guid.NewGuid(), 0, "A", " ", "1", 0, 0, Guid.NewGuid()))
            .Code.ShouldBe("seat.invalid_label");
    }
}
