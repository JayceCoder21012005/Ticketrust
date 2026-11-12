using TicketRush.Application.Features.Tickets;
using TicketRush.Domain.Events;
using TicketRush.Domain.Tickets;

namespace TicketRush.Application.Tests.Tickets;

public sealed class TicketDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_valid_ticket_of_a_running_published_event_can_show_its_qr()
    {
        TicketDisplay.CanShowQr(TicketStatus.Valid, EventStatus.Published, Now.AddHours(1), Now).ShouldBeTrue();
    }

    [Theory]
    [InlineData(TicketStatus.CheckedIn)]
    [InlineData(TicketStatus.Listed)]
    [InlineData(TicketStatus.Voided)]
    public void Only_a_valid_ticket_can_show_its_qr(TicketStatus status)
    {
        TicketDisplay.CanShowQr(status, EventStatus.Published, Now.AddHours(1), Now).ShouldBeFalse();
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Ended)]
    [InlineData(EventStatus.Cancelled)]
    public void Only_a_published_event_shows_qr_codes(EventStatus status)
    {
        TicketDisplay.CanShowQr(TicketStatus.Valid, status, Now.AddHours(1), Now).ShouldBeFalse();
    }

    [Fact]
    public void The_event_end_is_exclusive()
    {
        TicketDisplay.CanShowQr(TicketStatus.Valid, EventStatus.Published, Now.AddTicks(1), Now).ShouldBeTrue();
        TicketDisplay.CanShowQr(TicketStatus.Valid, EventStatus.Published, Now, Now).ShouldBeFalse();
        TicketDisplay.CanShowQr(TicketStatus.Valid, EventStatus.Published, Now.AddSeconds(-1), Now).ShouldBeFalse();
    }
}
