using TicketRush.Domain.Notifications;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Notifications;

public sealed class EmailDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static EmailDelivery New() =>
        EmailDelivery.Create(Guid.CreateVersion7(), "a@example.com", "order-paid", "{\"orderId\":1}", "order-paid:1", Now);

    [Fact]
    public void EmailDelivery_create_starts_pending_and_due_now()
    {
        var delivery = New();

        delivery.Id.ShouldNotBe(Guid.Empty);
        delivery.Status.ShouldBe(EmailDeliveryStatus.Pending);
        delivery.Attempts.ShouldBe(0);
        delivery.NextAttemptAt.ShouldBe(Now);
        delivery.LastError.ShouldBeNull();
        delivery.SentAt.ShouldBeNull();
        delivery.ToAddress.ShouldBe("a@example.com");
        delivery.Template.ShouldBe("order-paid");
        delivery.ModelJson.ShouldBe("{\"orderId\":1}");
        delivery.DedupeKey.ShouldBe("order-paid:1");
    }

    [Fact]
    public void EmailDelivery_create_rejects_a_blank_address_template_model_or_key()
    {
        var user = Guid.CreateVersion7();
        Should.Throw<DomainException>(() => EmailDelivery.Create(user, " ", "t", "{}", "k", Now)).Code.ShouldBe("email_delivery.invalid");
        Should.Throw<DomainException>(() => EmailDelivery.Create(user, "a@example.com", " ", "{}", "k", Now));
        Should.Throw<DomainException>(() => EmailDelivery.Create(user, "a@example.com", "t", " ", "k", Now));
        Should.Throw<DomainException>(() => EmailDelivery.Create(user, "a@example.com", "t", "{}", " ", Now));
        Should.Throw<DomainException>(() => EmailDelivery.Create(Guid.Empty, "a@example.com", "t", "{}", "k", Now));
    }

    [Fact]
    public void EmailDelivery_RecordFailure_increments_attempts_and_schedules_next_attempt()
    {
        var delivery = New();

        delivery.RecordFailure(Now.AddSeconds(10), "smtp down", TimeSpan.FromMinutes(2), deadLetter: false);
        delivery.RecordFailure(Now.AddMinutes(3), "smtp still down", TimeSpan.FromMinutes(4), deadLetter: false);

        delivery.Attempts.ShouldBe(2);
        delivery.Status.ShouldBe(EmailDeliveryStatus.Pending);
        delivery.NextAttemptAt.ShouldBe(Now.AddMinutes(7));
        delivery.LastError.ShouldBe("smtp still down");
    }

    [Fact]
    public void EmailDelivery_RecordFailure_cuts_the_error_to_the_column_width()
    {
        var delivery = New();

        delivery.RecordFailure(Now, new string('x', EmailDelivery.MaxErrorLength + 50), TimeSpan.FromSeconds(1), deadLetter: false);

        delivery.LastError!.Length.ShouldBe(EmailDelivery.MaxErrorLength);
    }

    [Fact]
    public void EmailDelivery_RecordFailure_with_deadLetter_marks_DeadLettered()
    {
        var delivery = New();

        delivery.RecordFailure(Now, "mailbox does not exist", TimeSpan.FromMinutes(2), deadLetter: true);

        delivery.Status.ShouldBe(EmailDeliveryStatus.DeadLettered);
        delivery.Attempts.ShouldBe(1);
        delivery.LastError.ShouldBe("mailbox does not exist");
        delivery.NextAttemptAt.ShouldBe(Now);
    }

    [Fact]
    public void EmailDelivery_MarkSent_records_the_time_and_leaves_pending()
    {
        var delivery = New();
        delivery.RecordFailure(Now, "timeout", TimeSpan.FromSeconds(30), deadLetter: false);

        delivery.MarkSent(Now.AddMinutes(1));

        delivery.Status.ShouldBe(EmailDeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public void EmailDelivery_that_left_pending_cannot_be_sent_or_failed_again()
    {
        var sent = New();
        sent.MarkSent(Now);
        var parked = New();
        parked.RecordFailure(Now, "bad", TimeSpan.Zero, deadLetter: true);

        Should.Throw<DomainException>(() => sent.MarkSent(Now)).Code.ShouldBe("email_delivery.illegal_transition");
        Should.Throw<DomainException>(() => sent.RecordFailure(Now, "x", TimeSpan.Zero, deadLetter: false));
        Should.Throw<DomainException>(() => parked.MarkSent(Now));
        Should.Throw<DomainException>(() => parked.RecordFailure(Now, "x", TimeSpan.Zero, deadLetter: true));
    }
}
