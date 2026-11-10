using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using static TicketRush.Domain.Tests.Orders.OrderTestData;

namespace TicketRush.Domain.Tests.Orders;

public sealed class PaymentAttemptTests
{
    private static PaymentAttempt Create(DateTimeOffset? orderExpiresAt = null) =>
        PaymentAttempt.Create(Guid.CreateVersion7(), "vnpay", "sess-1", RedirectUrl, new Money(1_600_000), orderExpiresAt ?? ExpiresAt, Now);

    private const string RedirectUrl = "http://localhost:3000/mock-gateway/sess-1";

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
        attempt.RedirectUrl.ShouldBe(RedirectUrl);
    }

    [Fact]
    public void Create_refuses_a_window_that_has_already_closed()
    {
        Should.Throw<DomainException>(() => Create(Now.AddSeconds(30))).Code.ShouldBe("payment.window_closed");
    }

    [Theory]
    [InlineData("", "s", RedirectUrl)]
    [InlineData("p", " ", RedirectUrl)]
    [InlineData("p", "s", " ")]
    [InlineData("p", "s", "/relative/path")]
    [InlineData("p", "s", "javascript:alert(1)")]
    public void Create_requires_provider_session_id_and_an_absolute_http_redirect_url(string provider, string session, string redirect)
    {
        Should.Throw<DomainException>(() =>
            PaymentAttempt.Create(Guid.CreateVersion7(), provider, session, redirect, new Money(1), ExpiresAt, Now))
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
    public void MarkSucceeded_is_legal_from_pending_failed_and_expired_because_webhooks_arrive_out_of_order(PaymentAttemptStatus from)
    {
        var attempt = InStatus(from);

        if (from is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.RefundPending or PaymentAttemptStatus.Refunded)
        {
            Should.Throw<DomainException>(attempt.MarkSucceeded).Code.ShouldBe("payment.illegal_transition");
            attempt.Status.ShouldBe(from);
            return;
        }

        attempt.MarkSucceeded();
        attempt.Status.ShouldBe(PaymentAttemptStatus.Succeeded);
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void RequestRefund_sends_back_money_that_reached_a_session_which_cannot_pay_for_its_order(PaymentAttemptStatus from)
    {
        var attempt = InStatus(from);
        attempt.DequeueDomainEvents();

        if (from is PaymentAttemptStatus.Succeeded or PaymentAttemptStatus.RefundPending or PaymentAttemptStatus.Refunded)
        {
            Should.Throw<DomainException>(() => attempt.RequestRefund(RefundReasons.DuplicatePayment, attempt.Amount)).Code.ShouldBe("payment.illegal_transition");
            attempt.Status.ShouldBe(from);
            attempt.DomainEvents.ShouldBeEmpty();
            return;
        }

        attempt.RequestRefund(RefundReasons.DuplicatePayment, new Money(1_500_000));

        attempt.Status.ShouldBe(PaymentAttemptStatus.RefundPending);
        attempt.DomainEvents.ShouldHaveSingleItem().ShouldBe(new PaymentRefundRequested(
            attempt.Id, attempt.OrderId, "vnpay", "sess-1", 1_500_000, "VND", RefundReasons.DuplicatePayment));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RequestRefund_needs_a_reason(string reason)
    {
        var attempt = Create();

        Should.Throw<DomainException>(() => attempt.RequestRefund(reason, attempt.Amount)).Code.ShouldBe("payment.invalid_refund");
        attempt.Status.ShouldBe(PaymentAttemptStatus.Pending);
    }

    [Theory]
    [MemberData(nameof(AllStatuses))]
    public void MarkRefunded_is_legal_only_from_refund_pending(PaymentAttemptStatus from)
    {
        var attempt = InStatus(from);

        if (from != PaymentAttemptStatus.RefundPending)
        {
            Should.Throw<DomainException>(attempt.MarkRefunded).Code.ShouldBe("payment.illegal_transition");
            return;
        }

        attempt.MarkRefunded();
        attempt.Status.ShouldBe(PaymentAttemptStatus.Refunded);
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
            case PaymentAttemptStatus.RefundPending:
                attempt.RequestRefund(RefundReasons.DuplicatePayment, attempt.Amount);
                break;
            case PaymentAttemptStatus.Refunded:
                attempt.RequestRefund(RefundReasons.DuplicatePayment, attempt.Amount);
                attempt.MarkRefunded();
                break;
        }

        return attempt;
    }
}
