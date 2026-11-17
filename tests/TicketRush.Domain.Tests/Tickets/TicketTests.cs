using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;

namespace TicketRush.Domain.Tests.Tickets;

public sealed class TicketTests
{
    private static readonly Guid OrderId = Guid.CreateVersion7();
    private static readonly Guid EventId = Guid.CreateVersion7();
    private static readonly Guid TierId = Guid.CreateVersion7();
    private static readonly Guid OwnerId = Guid.CreateVersion7();

    private static readonly Dictionary<string, Action<Ticket>> Methods = new()
    {
        [nameof(Ticket.CheckIn)] = t => t.CheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow),
        [nameof(Ticket.List)] = t => t.List(),
        [nameof(Ticket.Unlist)] = t => t.Unlist(),
        [nameof(Ticket.Void)] = t => t.Void(),
        [nameof(Ticket.VoidForCancelledEvent)] = t => t.VoidForCancelledEvent(),
    };

    private static readonly Dictionary<string, Dictionary<TicketStatus, TicketStatus>> Legal = new()
    {
        [nameof(Ticket.CheckIn)] = new() { [TicketStatus.Valid] = TicketStatus.CheckedIn },
        [nameof(Ticket.List)] = new() { [TicketStatus.Valid] = TicketStatus.Listed },
        [nameof(Ticket.Unlist)] = new() { [TicketStatus.Listed] = TicketStatus.Valid },
        [nameof(Ticket.Void)] = new()
        {
            [TicketStatus.Valid] = TicketStatus.Voided,
            [TicketStatus.Listed] = TicketStatus.Voided,
        },

        // A cancelled event voids every ticket, a checked-in one too (the whole order is refunded).
        [nameof(Ticket.VoidForCancelledEvent)] = new()
        {
            [TicketStatus.Valid] = TicketStatus.Voided,
            [TicketStatus.Listed] = TicketStatus.Voided,
            [TicketStatus.CheckedIn] = TicketStatus.Voided,
        },
    };

    public static TheoryData<string, TicketStatus> Matrix()
    {
        var data = new TheoryData<string, TicketStatus>();
        foreach (var method in Methods.Keys)
        {
            foreach (var status in Enum.GetValues<TicketStatus>())
            {
                data.Add(method, status);
            }
        }

        return data;
    }

    [Fact]
    public void The_statuses_voidable_for_a_cancelled_event_are_exactly_those_the_domain_method_accepts()
    {
        foreach (var status in Enum.GetValues<TicketStatus>())
        {
            var accepted = Legal[nameof(Ticket.VoidForCancelledEvent)].ContainsKey(status);
            Ticket.StatusesVoidableForCancelledEvent.Contains(status).ShouldBe(accepted, status.ToString());
        }
    }

    [Fact]
    public void Issue_creates_a_valid_ticket_with_a_32_byte_random_secret()
    {
        var ticket = Issue();

        ticket.Status.ShouldBe(TicketStatus.Valid);
        ticket.OrderId.ShouldBe(OrderId);
        ticket.EventId.ShouldBe(EventId);
        ticket.TierId.ShouldBe(TierId);
        ticket.SeatIndex.ShouldBe(4);
        ticket.OwnerUserId.ShouldBe(OwnerId);
        ticket.QrSecret.Length.ShouldBe(32);
        Ticket.QrSecretLength.ShouldBe(32);
    }

    [Fact]
    public void Every_ticket_gets_its_own_secret()
    {
        Issue().QrSecret.ShouldNotBe(Issue().QrSecret);
    }

    [Fact]
    public void General_admission_tickets_have_no_seat()
    {
        Issue(null).SeatIndex.ShouldBeNull();
    }

    [Fact]
    public void Issue_rejects_empty_references_and_negative_seats()
    {
        Should.Throw<DomainException>(() => Ticket.Issue(Guid.Empty, EventId, TierId, 1, OwnerId)).Code.ShouldBe("ticket.invalid_reference");
        Should.Throw<DomainException>(() => Issue(-1)).Code.ShouldBe("ticket.invalid_seat");
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_status_and_method_pair_behaves_per_the_table(string method, TicketStatus from)
    {
        var ticket = InStatus(from);

        if (Legal[method].TryGetValue(from, out var to))
        {
            Methods[method](ticket);

            ticket.Status.ShouldBe(to);
            return;
        }

        Should.Throw<DomainException>(() => Methods[method](ticket)).Code.ShouldBe("ticket.illegal_transition");
        ticket.Status.ShouldBe(from);
    }

    private static Ticket Issue(int? seat = 4) => Ticket.Issue(OrderId, EventId, TierId, seat, OwnerId);

    private static Ticket InStatus(TicketStatus status)
    {
        var ticket = Issue();
        switch (status)
        {
            case TicketStatus.CheckedIn:
                ticket.CheckIn(Guid.NewGuid(), DateTimeOffset.UtcNow);
                break;
            case TicketStatus.Listed:
                ticket.List();
                break;
            case TicketStatus.Voided:
                ticket.Void();
                break;
        }

        return ticket;
    }
}
