using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using static TicketRush.Domain.Tests.Orders.OrderTestData;

namespace TicketRush.Domain.Tests.Orders;

public sealed class PaymentAttemptTests
{
    private static PaymentAttempt Create(DateTimeOffset? orderExpiresAt = null) =>
        PaymentAttempt.Create(Guid.CreateVersion7(), "vnpay", "sess-1", new Money(1_600_000), orderExpiresAt ?? ExpiresAt, Now);

    public static TheoryData<PaymentAttemptStatus> AllStatuses() => [.. Enum.GetValues<PaymentAttemptStatus>()];

    [Fact]
    public void Create_expires_30_seconds_before_the_order_so_the_gateway_normally_refuses_late_payment()
    {
        var attempt = Create();

        attempt.ExpiresAt.ShouldBe(ExpiresAt.AddSeconds(-30));
        attempt.Status.ShouldBe(PaymentAttemptStatus.Pending);
        attempt.Provider.ShouldBe("vnpay");
        attempt.ProviderSessionId.ShouldBe("sess-1");
        attempt.Amount.ShouldBe(new Money(1_600_000));
    }

    [Fact]
    public void Create_refuses_a_window_that_has_already_closed()
    {
        Should.Throw<DomainException>(() => Create(Now.AddSeconds(30))).Code.ShouldBe("payment.window_closed");
    }

    [Theory]
    [InlineData("", "s")]
    [InlineData("p", " ")]
    public void Create_requires_provider_and_session_id(string provider, string session)
    {
        Should.Throw<DomainException>(() =>
            PaymentAttempt.Create(Guid.CreateVersion7(), provider, session, new Money(1), ExpiresAt, Now))
            .Code.ShouldBe("payment.invalid_attempt");
    }

    [Fact]
    public void IsLive_only_while_pending_and_before_expiry()
    {
        var attempt = Create();

        attempt.IsLive(Now).ShouldBeTrue();
        attempt.IsLive(attempt.ExpiresAt).ShouldBeFalse();
        attempt.MarkFailed();
        attempt.IsLive(Now).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void MarkSucceeded_is_legal_from_anything_but_succeeded_because_webhooks_arrive_out_of_order(PaymentAttemptStatus from)
    {
        var attempt = InStatus(from);

        if (from == PaymentAttemptStatus.Succeeded)
        {
            Should.Throw<DomainException>(attempt.MarkSucceeded).Code.ShouldBe("payment.illegal_transition");
            return;
        }

        attempt.MarkSucceeded();
        attempt.Status.ShouldBe(PaymentAttemptStatus.Succeeded);
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void MarkFailed_and_MarkExpired_are_legal_only_from_pending(PaymentAttemptStatus from)
    {
        if (from == PaymentAttemptStatus.Pending)
        {
            InStatus(from).MarkFailed();
            InStatus(from).MarkExpired();
            return;
        }

        Should.Throw<DomainException>(InStatus(from).MarkFailed).Code.ShouldBe("payment.illegal_transition");
        Should.Throw<DomainException>(InStatus(from).MarkExpired).Code.ShouldBe("payment.illegal_transition");
    }

    private static PaymentAttempt InStatus(PaymentAttemptStatus status)
    {
        var attempt = Create();
        switch (status)
        {
            case PaymentAttemptStatus.Succeeded:
                attempt.MarkSucceeded();
                break;
            case PaymentAttemptStatus.Failed:
                attempt.MarkFailed();
                break;
            case PaymentAttemptStatus.Expired:
                attempt.MarkExpired();
                break;
        }

        return attempt;
    }
}
