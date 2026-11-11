using TicketRush.Domain.Notifications;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Notifications;

public sealed class ReminderAndFollowTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly Guid EventId = Guid.CreateVersion7();

    [Fact]
    public void ReminderDispatch_create_raises_ReminderDue()
    {
        var dispatch = ReminderDispatch.Create(UserId, EventId, ReminderKind.SaleOpens30m, Now);

        dispatch.UserId.ShouldBe(UserId);
        dispatch.EventId.ShouldBe(EventId);
        dispatch.Kind.ShouldBe(ReminderKind.SaleOpens30m);
        dispatch.CreatedAt.ShouldBe(Now);
        dispatch.DomainEvents.ShouldHaveSingleItem().ShouldBe(new ReminderDue(UserId, EventId, ReminderKind.SaleOpens30m));
    }

    [Fact]
    public void ReminderDispatch_create_rejects_missing_ids()
    {
        Should.Throw<DomainException>(() => ReminderDispatch.Create(Guid.Empty, EventId, ReminderKind.EventStarts24h, Now)).Code.ShouldBe("reminder.invalid");
        Should.Throw<DomainException>(() => ReminderDispatch.Create(UserId, Guid.Empty, ReminderKind.EventStarts24h, Now));
    }

    [Fact]
    public void EventFollow_create_keeps_the_pair_and_the_time()
    {
        var follow = EventFollow.Create(UserId, EventId, Now);

        follow.UserId.ShouldBe(UserId);
        follow.EventId.ShouldBe(EventId);
        follow.CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public void EventFollow_create_rejects_missing_ids()
    {
        Should.Throw<DomainException>(() => EventFollow.Create(Guid.Empty, EventId, Now)).Code.ShouldBe("follow.invalid");
        Should.Throw<DomainException>(() => EventFollow.Create(UserId, Guid.Empty, Now));
    }
}
