using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;

namespace TicketRush.Domain.Tests.Users;

public sealed class AdminDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_application_is_decided_once_with_a_reason_and_raises_the_decision()
    {
        var application = OrganizerApplication.Submit(Guid.NewGuid(), "  Công ty A  ", "0901234567", "Hoà nhạc");
        application.CompanyName.ShouldBe("Công ty A");
        application.Status.ShouldBe(OrganizerApplicationStatus.Pending);
        Should.Throw<DomainException>(() => application.Approve(Guid.NewGuid(), " ", Now)).Code.ShouldBe("admin.reason_required");

        var admin = Guid.NewGuid();
        application.Approve(admin, "Đủ hồ sơ", Now);

        application.Status.ShouldBe(OrganizerApplicationStatus.Approved);
        (application.ReviewedByUserId, application.ReviewedAt).ShouldBe((admin, Now));
        application.DomainEvents.OfType<OrganizerApplicationDecided>().Single().Decision.ShouldBe(OrganizerApplicationStatus.Approved);
        Should.Throw<DomainException>(() => application.Reject(admin, "đổi ý", Now)).Code.ShouldBe("organizer_application.decided");
    }

    [Fact]
    public void An_application_needs_a_company_and_a_phone() =>
        Should.Throw<DomainException>(() => OrganizerApplication.Submit(Guid.NewGuid(), "", "0901234567", "x")).Code.ShouldBe("organizer_application.invalid");

    [Fact]
    public void A_flag_is_resolved_once()
    {
        var flag = AccountFlag.Raise(Guid.NewGuid(), AccountFlag.SharedPhone, "4 accounts", Now);

        flag.Resolve("dismissed: family", Now);

        flag.ResolvedAt.ShouldBe(Now);
        flag.Resolution.ShouldBe("dismissed: family");
        Should.Throw<DomainException>(() => flag.Resolve("again", Now)).Code.ShouldBe("account_flag.resolved");
    }
}
