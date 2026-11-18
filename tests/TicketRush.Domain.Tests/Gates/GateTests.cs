using TicketRush.Domain.Gates;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Tests.Gates;

public sealed class GateTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 20, 19, 42, 0, TimeSpan.Zero);

    [Fact]
    public void Check_in_records_who_and_when()
    {
        var staff = Guid.CreateVersion7();
        var ticket = Ticket.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 3, Guid.CreateVersion7());

        ticket.CheckIn(staff, At);

        Assert.Equal(TicketStatus.CheckedIn, ticket.Status);
        Assert.Equal(At, ticket.CheckedInAt);
        Assert.Equal(staff, ticket.CheckedInByUserId);
    }

    [Fact]
    public void Check_in_needs_a_staff_user()
    {
        var ticket = Ticket.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7());

        Assert.Equal("ticket.invalid_reference", Assert.Throws<DomainException>(() => ticket.CheckIn(Guid.Empty, At)).Code);
        Assert.Equal(TicketStatus.Valid, ticket.Status);
    }

    [Fact]
    public void A_second_check_in_is_refused_and_keeps_the_first_time()
    {
        var ticket = Ticket.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), null, Guid.CreateVersion7());
        ticket.CheckIn(Guid.CreateVersion7(), At);

        Assert.Throws<DomainException>(() => ticket.CheckIn(Guid.CreateVersion7(), At.AddMinutes(1)));
        Assert.Equal(At, ticket.CheckedInAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_gate_name_cannot_be_blank(string name) =>
        Assert.Equal("gate.invalid_name", Assert.Throws<DomainException>(() => GateAssignment.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), name, At)).Code);

    [Fact]
    public void A_gate_name_is_trimmed_and_can_be_changed()
    {
        var assignment = GateAssignment.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "  Cổng A ", At);
        Assert.Equal("Cổng A", assignment.GateName);

        assignment.Rename("Cổng B");
        Assert.Equal("Cổng B", assignment.GateName);
        Assert.Throws<DomainException>(() => assignment.Rename(new string('x', GateAssignment.GateNameMaxLength + 1)));
    }

    [Fact]
    public void A_log_line_needs_an_event_and_a_staff_user()
    {
        Assert.Throws<DomainException>(() => CheckIn.Record(null, Guid.Empty, Guid.CreateVersion7(), "A", At, ScanResult.Invalid));
        var line = CheckIn.Record(null, Guid.CreateVersion7(), Guid.CreateVersion7(), "A", At, ScanResult.Invalid, "bad_format");
        Assert.Null(line.TicketId);
        Assert.Equal(ScanResult.Invalid, line.Result);
    }
}
