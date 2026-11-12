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

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    public void EmailDelivery_RecordFailure_with_blank_error_still_records_the_attempt(string? error, bool deadLetter)
    {
        var delivery = New();

        delivery.RecordFailure(Now, error!, TimeSpan.FromMinutes(2), deadLetter);

        delivery.Attempts.ShouldBe(1);
        delivery.LastError.ShouldBe(EmailDelivery.UnknownError);
        delivery.Status.ShouldBe(deadLetter ? EmailDeliveryStatus.DeadLettered : EmailDeliveryStatus.Pending);
        delivery.NextAttemptAt.ShouldBe(deadLetter ? Now : Now.AddMinutes(2));
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
    public void EmailDelivery_MarkSent_sets_status_Sent_and_records_the_time()
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

    [Fact]
    public void EmailDelivery_create_records_its_creation_time_and_no_expiry_by_default()
    {
        var delivery = New();

        delivery.CreatedAt.ShouldBe(Now);
        delivery.ExpiresAt.ShouldBeNull();
        delivery.IsExpired(Now.AddYears(1)).ShouldBeFalse();
    }

    [Fact]
    public void EmailDelivery_with_an_expiry_is_expired_from_that_moment_on()
    {
        var delivery = EmailDelivery.Create(Guid.CreateVersion7(), "a@example.com", "reminder-sale-opens", "{}", "reminder:1", Now, Now.AddMinutes(30));

        delivery.ExpiresAt.ShouldBe(Now.AddMinutes(30));
        delivery.IsExpired(Now.AddMinutes(30).AddTicks(-1)).ShouldBeFalse();
        delivery.IsExpired(Now.AddMinutes(30)).ShouldBeTrue();
    }

    [Fact]
    public void EmailDelivery_MarkExpired_parks_it_without_counting_an_attempt()
    {
        var delivery = New();
        delivery.RecordFailure(Now, "timeout", TimeSpan.FromSeconds(30), deadLetter: false);

        delivery.MarkExpired();

        delivery.Status.ShouldBe(EmailDeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe(EmailDelivery.ExpiredError);
        delivery.Attempts.ShouldBe(1);
        delivery.SentAt.ShouldBeNull();
        Should.Throw<DomainException>(() => delivery.MarkExpired()).Code.ShouldBe("email_delivery.illegal_transition");
    }
}
