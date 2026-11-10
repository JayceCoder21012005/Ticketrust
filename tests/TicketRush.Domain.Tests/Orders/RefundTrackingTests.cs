using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using static TicketRush.Domain.Tests.Orders.OrderTestData;

namespace TicketRush.Domain.Tests.Orders;

/// <summary>Retry bookkeeping of the refund worker (B8) on both refund targets: an order and a single payment session.</summary>
public sealed class RefundTrackingTests
{
    private static readonly DateTimeOffset Later = Now.AddMinutes(1);

    private static PaymentAttempt Attempt() =>
        PaymentAttempt.Create(Guid.CreateVersion7(), "mock", "sess-1", "http://localhost:3000/mock-gateway/sess-1", new Money(1_600_000), ExpiresAt, Now);

    private static PaymentAttempt RefundPendingAttempt(long amount = 1_500_000)
    {
        var attempt = Attempt();
        attempt.RequestRefund(RefundReasons.AmountMismatch, new Money(amount));
        attempt.DequeueDomainEvents();
        return attempt;
    }

    public static TheoryData<string> Targets() => ["order", "attempt"];

    private static IRefundable Target(string kind) => kind == "order" ? InStatus(OrderStatus.RefundPending) : RefundPendingAttempt();

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_new_refund_is_due_at_once_with_nothing_recorded(string kind)
    {
        var target = Target(kind);

        target.Refund.ShouldBe(RefundTracking.None);
        target.Refund.IsDue(Now).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_decline_counts_keeps_the_reason_and_waits_until_the_retry_time(string kind)
    {
        var target = Target(kind);

        target.RecordRefundDecline("payment_not_succeeded", Later);

        target.Refund.ShouldBe(new RefundTracking(1, Later, "payment_not_succeeded", false));
        target.Refund.IsDue(Later.AddTicks(-1)).ShouldBeFalse();
        target.Refund.IsDue(Later).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_decline_without_a_retry_time_parks_the_refund_for_a_person_and_it_is_never_due(string kind)
    {
        var target = Target(kind);
        target.RecordRefundDecline("invalid_amount", Later);

        target.RecordRefundDecline("invalid_amount", retryAt: null);

        target.Refund.ShouldBe(new RefundTracking(2, null, "invalid_amount", true));
        target.Refund.IsDue(Later.AddYears(1)).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Parking_without_a_gateway_call_keeps_the_count(string kind)
    {
        var target = Target(kind);

        target.ParkRefund("no_succeeded_attempt");

        target.Refund.ShouldBe(new RefundTracking(0, null, "no_succeeded_attempt", true));
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void Resuming_a_parked_refund_makes_it_due_again_with_a_fresh_count(string kind)
    {
        var target = Target(kind);
        target.RecordRefundDecline("invalid_amount", retryAt: null);

        target.ResumeRefund();

        target.Refund.ShouldBe(new RefundTracking(0, null, "invalid_amount", false));
        target.Refund.IsDue(Now).ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Targets))]
    public void A_long_gateway_reason_is_cut_to_the_column_width(string kind)
    {
        var target = Target(kind);

        target.RecordRefundDecline(new string('x', 2_000), Later);

        target.Refund.LastError!.Length.ShouldBe(RefundTracking.MaxErrorLength);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.Paid)]
    [InlineData(OrderStatus.Refunded)]
    [InlineData(OrderStatus.Expired)]
    public void Only_an_order_waiting_for_its_refund_tracks_one(OrderStatus status)
    {
        var order = InStatus(status);

        Should.Throw<DomainException>(() => order.RecordRefundDecline("x", Later)).Code.ShouldBe("order.illegal_transition");
        Should.Throw<DomainException>(() => order.ParkRefund("x")).Code.ShouldBe("order.illegal_transition");
        Should.Throw<DomainException>(order.ResumeRefund).Code.ShouldBe("order.illegal_transition");
        order.Refund.ShouldBe(RefundTracking.None);
    }

    [Fact]
    public void Only_a_session_waiting_for_its_refund_tracks_one()
    {
        var attempt = Attempt();
        attempt.MarkSucceeded();

        Should.Throw<DomainException>(() => attempt.RecordRefundDecline("x", Later)).Code.ShouldBe("payment.illegal_transition");
        Should.Throw<DomainException>(() => attempt.ParkRefund("x")).Code.ShouldBe("payment.illegal_transition");
        Should.Throw<DomainException>(attempt.ResumeRefund).Code.ShouldBe("payment.illegal_transition");
        attempt.Refund.ShouldBe(RefundTracking.None);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void A_decline_or_park_needs_a_reason(string reason)
    {
        var order = InStatus(OrderStatus.RefundPending);

        Should.Throw<ArgumentException>(() => order.RecordRefundDecline(reason, Later));
        Should.Throw<ArgumentException>(() => order.ParkRefund(reason));
    }

    [Fact]
    public void A_session_refund_remembers_the_amount_it_must_send_back()
    {
        var attempt = Attempt();
        attempt.RefundAmount.ShouldBeNull();

        attempt.RequestRefund(RefundReasons.AmountMismatch, new Money(1_500_000));

        attempt.RefundAmount.ShouldBe(new Money(1_500_000));
    }

    [Fact]
    public void A_refunded_session_raises_PaymentRefunded_with_the_amount_sent_back()
    {
        var attempt = RefundPendingAttempt(1_200_000);

        attempt.MarkRefunded();

        attempt.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBe(new PaymentRefunded(attempt.Id, attempt.OrderId, 1_200_000, "VND"));
    }
}
