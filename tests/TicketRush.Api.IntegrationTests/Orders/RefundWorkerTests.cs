using Microsoft.EntityFrameworkCore;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Orders;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence.Migrations;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// Spec 08 RefundWorker against Postgres, Redis and the real mock gateway on a fake clock: exactly one refund per order or
/// session even with two workers or a crash between the gateway call and the commit, declines backed off and finally parked
/// (never dropped), outages retried without counting.
/// </summary>
public sealed class RefundWorkerTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    private CheckoutWorkersHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await CheckoutWorkersHarness.CreateAsync(containers, new Dictionary<string, string?>
    {
        ["Refunds:MaxDeclines"] = "3",
        ["Refunds:RetryBaseDelay"] = BaseDelay.ToString(),
        ["Refunds:RetryMaxDelay"] = MaxDelay.ToString(),
    });

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private Task<int> TickAsync() => _h.Refunds().RunOnceAsync(Ct);

    /// <summary>The buyer paid at the gateway but the order could not be fulfilled (what the fulfil path leaves behind).</summary>
    private async Task<CheckoutWorkersHarness.Placed> OrderOwedRefundAsync(string how = "unfulfillable", bool paidAtGateway = true)
    {
        var placed = await _h.PlaceAsync([_h.GaUnits(2)], paidAtGateway ? MockOutcome.SucceededDelayed : null);
        if (how == "event_cancelled")
        {
            (await _h.DeliverSucceededAsync(placed)).ShouldBe(PaymentWebhookOutcome.Paid);
            await _h.ChangeOrderAsync(placed.OrderId, o => o.RequestRefundForCancelledEvent());
            return placed;
        }

        await _h.ChangeOrderAsync(placed.OrderId, o => o.MarkUnfulfillable());
        await _h.ChangeAttemptAsync(placed.AttemptId, a => a.MarkSucceeded());
        return placed;
    }

    /// <summary>A paid order whose buyer paid a second session too: the fulfil path refunds only that session.</summary>
    private async Task<(CheckoutWorkersHarness.Placed First, CheckoutWorkersHarness.Placed Second)> DuplicatePaymentAsync()
    {
        var first = await _h.PlaceAsync([_h.Seat(_h.Seats[0])], MockOutcome.SucceededDelayed);
        var second = await _h.AddSessionAsync(first.OrderId);
        (await _h.Control.CompleteAsync(second.SessionId, MockOutcome.SucceededDelayed, Ct)).IsSuccess.ShouldBeTrue();
        (await _h.DeliverSucceededAsync(first)).ShouldBe(PaymentWebhookOutcome.Paid);
        (await _h.DeliverSucceededAsync(second)).ShouldBe(PaymentWebhookOutcome.RefundPending);
        return (first, second);
    }

    [Theory]
    [InlineData("unfulfillable")]
    [InlineData("event_cancelled")]
    public async Task An_order_owed_a_refund_is_refunded_at_the_gateway_with_the_order_id_as_key(string how)
    {
        var placed = await OrderOwedRefundAsync(how);
        using var metrics = _h.RecordMetrics();

        (await TickAsync()).ShouldBe(1);

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Refunded);
        state.Outbox.Count(t => t == nameof(OrderRefunded)).ShouldBe(1);
        _h.Spy.RefundKeys.ShouldBe([placed.OrderId.ToString()]);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);
        metrics.Counted("ticketrush.payments.refunds").ShouldBe(["target=order,outcome=refunded"]);

        (await TickAsync()).ShouldBe(0);
        _h.Spy.RefundCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Two_workers_at_once_refund_once_and_commit_once()
    {
        var placed = await OrderOwedRefundAsync();
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Spy.AfterRefund = async _ =>
        {
            // Both nodes have the gateway's answer before either commits.
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }

            await both.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        };

        await Task.WhenAll(TickAsync(), TickAsync());

        _h.Spy.RefundCalls.ShouldBe(2);
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.OutboxCountAsync(nameof(OrderRefunded))).ShouldBe(1);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount, "the idempotency key refunds the money once");
    }

    [Fact]
    public async Task A_crash_after_the_gateway_refunded_but_before_the_commit_is_finished_by_the_next_pass_without_a_second_refund()
    {
        var placed = await OrderOwedRefundAsync();
        var refundIds = new List<string>();
        _h.Spy.AfterRefund = result =>
        {
            refundIds.Add(result.RefundId);
            return refundIds.Count == 1 ? throw new InvalidOperationException("worker died before the commit") : Task.CompletedTask;
        };
        using var metrics = _h.RecordMetrics();

        var start = _h.Time.GetUtcNow();
        await TickAsync();

        var crashed = await _h.StateAsync(placed.OrderId);
        crashed.Status.ShouldBe(OrderStatus.RefundPending);
        crashed.Refund.ShouldBe(new RefundTracking(1, start + BaseDelay, "error: InvalidOperationException", false), "an unexpected failure backs off like a decline");
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);

        _h.Time.Advance(BaseDelay);
        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);
        refundIds.Count.ShouldBe(2);
        refundIds.Distinct().ShouldHaveSingleItem("the mock answers the same key with the same refund");
        _h.Spy.RefundKeys.Distinct().ShouldBe([placed.OrderId.ToString()]);
        metrics.Counted("ticketrush.payments.refunds").ShouldBe(["target=order,outcome=error", "target=order,outcome=refunded"]);
    }

    [Fact]
    public async Task A_session_refund_sends_back_that_session_with_its_own_key_and_leaves_the_order_alone()
    {
        var (first, second) = await DuplicatePaymentAsync();

        (await TickAsync()).ShouldBe(1);

        var attempt = await _h.AttemptAsync(second.AttemptId);
        attempt.Status.ShouldBe(PaymentAttemptStatus.Refunded);
        _h.Spy.RefundKeys.ShouldBe([$"attempt:{second.AttemptId}"]);
        (await _h.RefundedOnGatewayAsync(second.SessionId)).ShouldBe(second.Total.Amount);
        (await _h.RefundedOnGatewayAsync(first.SessionId)).ShouldBe(0);
        var order = await _h.StateAsync(first.OrderId);
        order.Status.ShouldBe(OrderStatus.Paid);
        order.Tickets.ShouldBe(1);
        order.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.Refunded]);
        (await _h.OutboxCountAsync(nameof(PaymentRefunded))).ShouldBe(1);
        (await _h.OutboxCountAsync(nameof(OrderRefunded))).ShouldBe(0);
    }

    [Fact]
    public async Task A_session_refund_sends_back_the_amount_the_request_recorded_not_the_session_amount()
    {
        // The provider reported less than the session amount (amount mismatch): only what was captured goes back.
        var placed = await _h.PlaceAsync([_h.GaUnits(2)], MockOutcome.SucceededDelayed);
        var captured = new TicketRush.Domain.Primitives.Money(placed.Total.Amount - 1_000);
        (await _h.DeliverSucceededAsync(placed, captured)).ShouldBe(PaymentWebhookOutcome.RefundPending);

        await TickAsync();

        (await _h.AttemptAsync(placed.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(captured.Amount);
    }

    [Fact]
    public async Task A_declined_refund_backs_off_is_parked_after_the_limit_without_being_lost_and_completes_once_resumed()
    {
        // The gateway refuses: the session never succeeded there (payment_not_succeeded).
        var placed = await OrderOwedRefundAsync(paidAtGateway: false);
        using var metrics = _h.RecordMetrics();
        var start = _h.Time.GetUtcNow();

        await TickAsync();
        (await _h.StateAsync(placed.OrderId)).Refund.ShouldBe(new RefundTracking(1, start + BaseDelay, "payment_not_succeeded", false));
        (await TickAsync()).ShouldBe(0, "not due before the backoff is over");
        _h.Spy.RefundCalls.ShouldBe(1);

        _h.Time.Advance(BaseDelay);
        await TickAsync();
        (await _h.StateAsync(placed.OrderId)).Refund.ShouldBe(new RefundTracking(2, start + BaseDelay + MaxDelay, "payment_not_succeeded", false), "doubled, then capped");

        _h.Time.Advance(MaxDelay);
        await TickAsync();
        var parked = await _h.StateAsync(placed.OrderId);
        parked.Status.ShouldBe(OrderStatus.RefundPending);
        parked.Refund.ShouldBe(new RefundTracking(3, null, "payment_not_succeeded", true));
        metrics.Counted("ticketrush.payments.refunds").ShouldBe(["target=order,outcome=declined", "target=order,outcome=declined", "target=order,outcome=parked"]);
        metrics.Gauge("ticketrush.payments.refunds_parked").ShouldBe(1);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        (await TickAsync()).ShouldBe(0, "a parked refund is not retried at full speed, or at all, until a person resumes it");
        _h.Spy.RefundCalls.ShouldBe(3);

        // A person fixes the cause (here: the payment does go through) and resumes the refund.
        (await _h.Control.CompleteAsync(placed.SessionId, MockOutcome.SucceededDelayed, Ct)).IsSuccess.ShouldBeTrue();
        await _h.ChangeOrderAsync(placed.OrderId, o => o.ResumeRefund());
        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);
        metrics.Gauge("ticketrush.payments.refunds_parked").ShouldBe(0);
    }

    [Fact]
    public async Task A_gateway_outage_is_retried_on_a_later_pass_and_never_counted_as_a_decline()
    {
        var placed = await OrderOwedRefundAsync();
        var session = await DuplicatePaymentAsync();
        _h.Gateway.SimulateOutage = true;
        using var metrics = _h.RecordMetrics();

        (await TickAsync()).ShouldBe(2);

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.Refund.ShouldBe(RefundTracking.None);
        (await _h.AttemptAsync(session.Second.AttemptId)).Refund.ShouldBe(RefundTracking.None);
        _h.Spy.RefundCalls.ShouldBe(1, "after the first outage the pass does not call the same gateway again");
        metrics.Counted("ticketrush.payments.refunds").ShouldBe(["target=order,outcome=skipped_outage", "target=session,outcome=skipped_outage"], ignoreOrder: true);

        _h.Gateway.SimulateOutage = false;
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.AttemptAsync(session.Second.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Refunded);
    }

    [Fact]
    public async Task An_order_owed_a_refund_without_a_succeeded_session_is_parked_not_crashed_on()
    {
        var placed = await _h.PlaceAsync([_h.GaUnits(1)], MockOutcome.SucceededDelayed);
        await _h.ChangeOrderAsync(placed.OrderId, o => o.MarkUnfulfillable());

        await TickAsync();

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.Refund.ShouldBe(new RefundTracking(0, null, "no_succeeded_session", true));
        _h.Spy.RefundCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_session_refund_without_a_recorded_amount_is_parked_instead_of_guessing()
    {
        var (_, second) = await DuplicatePaymentAsync();
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE payment_attempts SET refund_amount = NULL, refund_currency = NULL WHERE id = {second.AttemptId}", Ct));

        await TickAsync();

        var attempt = await _h.AttemptAsync(second.AttemptId);
        attempt.Status.ShouldBe(PaymentAttemptStatus.RefundPending);
        attempt.Refund.ShouldBe(new RefundTracking(0, null, "missing_refund_amount", true));
        _h.Spy.RefundCalls.ShouldBe(0);
    }

    // ================================================================ B8 fix round 1

    public static TheoryData<string> AdapterFailures() => ["InvalidOperationException", "ArgumentException"];

    [Theory]
    [MemberData(nameof(AdapterFailures))]
    public async Task An_adapter_that_throws_for_one_refund_does_not_block_the_next_and_the_failing_one_backs_off_then_parks(string failure)
    {
        var failing = await OrderOwedRefundAsync();
        var healthy = await OrderOwedRefundAsync();
        _h.Spy.BeforeRefund = key => key == failing.OrderId.ToString()
            ? throw (failure == "ArgumentException" ? new ArgumentException("bad adapter input") : new InvalidOperationException("adapter bug"))
            : Task.CompletedTask;
        var start = _h.Time.GetUtcNow();

        (await TickAsync()).ShouldBe(2);

        (await _h.StateAsync(healthy.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.StateAsync(failing.OrderId)).Refund.ShouldBe(new RefundTracking(1, start + BaseDelay, $"error: {failure}", false));
        (await TickAsync()).ShouldBe(0, "backing off, not retried at full speed");

        _h.Time.Advance(BaseDelay);
        await TickAsync();
        _h.Time.Advance(MaxDelay);
        await TickAsync();

        var parked = await _h.StateAsync(failing.OrderId);
        parked.Status.ShouldBe(OrderStatus.RefundPending);
        parked.Refund.ShouldBe(new RefundTracking(3, null, $"error: {failure}", true));
    }

    [Fact]
    public async Task A_thrown_gateway_rejection_counts_as_a_decline()
    {
        var placed = await OrderOwedRefundAsync();
        _h.Spy.BeforeRefund = _ => throw new PaymentRejectedException("refund refused");
        var start = _h.Time.GetUtcNow();

        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Refund.ShouldBe(new RefundTracking(1, start + BaseDelay, "rejected: refund refused", false));
    }

    [Fact]
    public async Task A_refund_through_a_provider_nobody_registered_is_parked()
    {
        var placed = await OrderOwedRefundAsync();
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE payment_attempts SET provider = 'ghost' WHERE id = {placed.AttemptId}", Ct));

        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Refund.ShouldBe(new RefundTracking(0, null, "unknown_provider", true));
        _h.Spy.RefundCalls.ShouldBe(0);
    }

    [Fact]
    public async Task An_order_with_two_succeeded_sessions_is_parked_rather_than_refunding_a_guess()
    {
        var placed = await OrderOwedRefundAsync();
        var extra = await _h.AddSessionAsync(placed.OrderId);
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE payment_attempts SET status = 'Succeeded' WHERE id = {extra.AttemptId}", Ct));

        await TickAsync();

        (await _h.StateAsync(placed.OrderId)).Refund.ShouldBe(new RefundTracking(0, null, "several_succeeded_sessions", true));
        _h.Spy.RefundCalls.ShouldBe(0);
    }

    [Fact]
    public async Task The_parked_gauge_is_reset_when_the_worker_stops_leading()
    {
        var placed = await OrderOwedRefundAsync();
        await _h.ChangeOrderAsync(placed.OrderId, o => o.ParkRefund("test"));
        using var metrics = _h.RecordMetrics();
        using var worker = _h.Refunds(TimeProvider.System);

        await worker.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (worker.TickCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, Ct);
        }

        metrics.Gauge("ticketrush.payments.refunds_parked").ShouldBe(1);
        await worker.StopAsync(CancellationToken.None);

        metrics.Gauge("ticketrush.payments.refunds_parked").ShouldBe(0, "a node that no longer leads must not keep reporting a stale count");
    }

    [Fact]
    public async Task The_migration_backfill_copies_a_pending_session_refund_amount_from_its_outbox_event()
    {
        var (_, second) = await DuplicatePaymentAsync();
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync(
            $"UPDATE payment_attempts SET refund_amount = NULL, refund_currency = NULL WHERE id = {second.AttemptId}", Ct));

        await _h.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(AddRefundTracking.BackfillRefundAmountsSql, Ct));

        var attempt = await _h.AttemptAsync(second.AttemptId);
        attempt.RefundAmount.ShouldBe(second.Total);
        (await TickAsync()).ShouldBe(1);
        (await _h.AttemptAsync(second.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Refunded);
    }

    [Fact]
    public async Task Only_the_leader_refunds()
    {
        using var first = _h.Refunds(TimeProvider.System);
        using var second = _h.Refunds(TimeProvider.System);
        await first.StartAsync(Ct);
        await second.StartAsync(Ct);
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), Ct);

            new[] { first.TickCount, second.TickCount }.Count(t => t > 0).ShouldBe(1);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            await second.StopAsync(CancellationToken.None);
        }
    }
}
