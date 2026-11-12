using TicketRush.Domain.Notifications;
using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Notifications;

public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static Notification New(string title = "Order paid", string body = "Your tickets are ready.", string? link = "/account/orders/1") =>
        Notification.Create(UserId, NotificationType.OrderPaid, title, body, link, "order-paid:1", Now);

    [Fact]
    public void Notification_create_trims_and_rejects_blank_title()
    {
        var notification = Notification.Create(UserId, NotificationType.OrderPaid, "  Order paid  ", "  Ready.  ", "  /account/orders/1  ", "order-paid:1", Now);

        notification.Id.ShouldNotBe(Guid.Empty);
        notification.UserId.ShouldBe(UserId);
        notification.Type.ShouldBe(NotificationType.OrderPaid);
        notification.Title.ShouldBe("Order paid");
        notification.Body.ShouldBe("Ready.");
        notification.Link.ShouldBe("/account/orders/1");
        notification.DedupeKey.ShouldBe("order-paid:1");
        notification.CreatedAt.ShouldBe(Now);
        notification.ReadAt.ShouldBeNull();

        Should.Throw<DomainException>(() => New(title: "   ")).Code.ShouldBe("notification.invalid");
        Should.Throw<DomainException>(() => New(title: ""));
    }

    [Fact]
    public void Notification_create_turns_a_blank_link_into_null()
    {
        New(link: "  ").Link.ShouldBeNull();
        New(link: null).Link.ShouldBeNull();
    }

    [Theory]
    [InlineData("https://evil.example/phish")]
    [InlineData("//evil.example/phish")]
    [InlineData("account/orders/1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/\\evil.example")]
    [InlineData("/\t/evil.example")]
    [InlineData("/\n/evil.example")]
    [InlineData("/\r/evil.example")]
    [InlineData("/account orders")]
    [InlineData("/account\u00a0orders")]
    [InlineData("/account/\u0000orders")]
    public void Notification_create_rejects_a_link_that_is_not_an_app_relative_path(string link)
    {
        Should.Throw<DomainException>(() => New(link: link)).Code.ShouldBe("notification.invalid");
    }

    [Fact]
    public void Notification_create_rejects_a_missing_user_body_or_dedupe_key_and_over_long_text()
    {
        Should.Throw<DomainException>(() => Notification.Create(Guid.Empty, NotificationType.OrderPaid, "t", "b", null, "k", Now));
        Should.Throw<DomainException>(() => New(body: " "));
        Should.Throw<DomainException>(() => Notification.Create(UserId, NotificationType.OrderPaid, "t", "b", null, " ", Now));
        Should.Throw<DomainException>(() => New(title: new string('x', Notification.MaxTitleLength + 1)));
        Should.Throw<DomainException>(() => New(body: new string('x', Notification.MaxBodyLength + 1)));
        Should.Throw<DomainException>(() => New(link: "/" + new string('x', Notification.MaxLinkLength)));
    }

    [Fact]
    public void Notification_mark_read_keeps_first_read_at()
    {
        var notification = New();

        notification.MarkRead(Now.AddMinutes(1));
        notification.MarkRead(Now.AddMinutes(5));

        notification.ReadAt.ShouldBe(Now.AddMinutes(1));
    }
}
