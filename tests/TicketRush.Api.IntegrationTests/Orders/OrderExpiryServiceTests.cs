extern alias WorkerAssembly;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Payments;
using WorkerAssembly::TicketRush.Worker.Payments;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// Spec 08 OrderExpiryService against Postgres, Redis and the real mock gateway on a fake clock: the gateway is asked
/// before an order expires, a late success goes through the webhook decision table (acceptance #4, #5), a webhook racing
/// the sweep wins, and a gateway outage never expires an order that may have been paid.
/// </summary>
public sealed class OrderExpiryServiceTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan PastExpiry = TimeSpan.FromMinutes(9);

    /// <summary>Default <c>Payments:Expiry:AttemptGrace</c>: how long a session of a closed order may stay Pending before it is asked again.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    private CheckoutWorkersHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await CheckoutWorkersHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task<int> SweepAsync() => await _h.Sweeper().RunOnceAsync(Ct);

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task An_order_whose_session_took_no_money_expires_and_the_session_is_closed_after_the_grace(string gatewayState)
    {
        var placed = gatewayState switch
        {
            // The session outlives the order, so the gateway still reads Pending when the sweep asks.
            "pending" => await _h.PlaceAsync([_h.Seat(_h.Seats[0])], sessionOutlivesOrder: TimeSpan.FromMinutes(5)),
            // The buyer pressed "Thất bại"; its webhook never arrived, so our attempt is still Pending.
            "failed" => await _h.PlaceAsync([_h.Seat(_h.Seats[0])], MockOutcome.Failed),
            _ => await _h.PlaceAsync([_h.Seat(_h.Seats[0])]),
        };
        _h.Time.Advance(PastExpiry);

        (await SweepAsync()).ShouldBe(1);

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Expired);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Pending], "only the order expires; the session is reconciled on its own");
        state.Tickets.ShouldBe(0);
        state.Outbox.ShouldBe([nameof(OrderExpired)]);
        _h.Spy.StatusCalls.ShouldBe(1);

        _h.Time.Advance(TimeSpan.FromMinutes(1));
        await SweepAsync();
        (await _h.StateAsync(placed.OrderId)).Attempts.ShouldBe([PaymentAttemptStatus.Pending], "still inside the grace");

        _h.Time.Advance(Grace);
        using var metrics = _h.RecordMetrics();
        await SweepAsync();

        (await _h.StateAsync(placed.OrderId)).Attempts.ShouldBe([gatewayState == "failed" ? PaymentAttemptStatus.Failed : PaymentAttemptStatus.Expired]);
        _h.Spy.StatusCalls.ShouldBe(2);
        metrics.Counted("ticketrush.payments.sessions_reconciled").ShouldBe(["outcome=closed"]);
    }

    [Fact]
    public async Task A_status_driven_decision_is_recorded_for_the_longest_session_id_a_payment_attempt_may_hold()
    {
        // provider_session_id is 200 wide; the status-read receipt id adds "status:", so processed_webhooks must hold more.
        var sessionId = new string('s', 200);
        var now = _h.Time.GetUtcNow();
        var total = new Money(_h.Ga.Price);
        var orderId = await _h.WithDbAsync(async db =>
        {
            var order = Order.Create(Guid.CreateVersion7(), _h.EventId, Guid.CreateVersion7(), [_h.GaUnits(1)], total,
                new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now + TimeSpan.FromMinutes(8), now);
            order.StartPayment(now);
            db.Orders.Add(order);
            db.PaymentAttempts.Add(PaymentAttempt.Create(order.Id, CheckoutWorkersHarness.Provider, sessionId, "http://localhost:3000/pay", total, order.ExpiresAt, now));
            await db.SaveChangesAsync(Ct);
            return order.Id;
        });
        var webhook = new WebhookEvent(ExpireOrderHandler.StatusEventId(sessionId), sessionId, WebhookEventType.Succeeded, total, now);

        await using var scope = _h.Host.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<FulfillOrderHandler>().HandleAsync(CheckoutWorkersHarness.Provider, webhook, Ct);

        outcome.ShouldBe(PaymentWebhookOutcome.Paid);
        (await _h.StateAsync(orderId)).Status.ShouldBe(OrderStatus.Paid);
        (await _h.ProcessedWebhooksAsync(webhook.ProviderEventId)).ShouldBe(1);
    }

    [Fact]
    public async Task A_late_success_with_the_seats_still_free_is_paid_through_the_webhook_path_and_never_expired()
    {
        // Acceptance #5: the buyer paid, the webhook is late (or lost); the gateway's status is the truth.
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[1]), _h.GaUnits(1)], MockOutcome.SucceededDelayed);
        _h.Time.Advance(PastExpiry);

        await SweepAsync();

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(2);
        state.Outbox.ShouldBe([nameof(OrderPaid)]);
        (await _h.ProcessedWebhooksAsync(ExpireOrderHandler.StatusEventId(placed.SessionId))).ShouldBe(1);

        // The provider's own (late) webhook then finds the session already decided.
        (await _h.DeliverSucceededAsync(placed)).ShouldBe(PaymentWebhookOutcome.Ignored);
        (await _h.StateAsync(placed.OrderId)).Tickets.ShouldBe(2);
        (await SweepAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_late_success_after_the_seat_was_resold_goes_to_refund_and_the_refund_worker_returns_the_money_once()
    {
        // Acceptance #4: RefundPending → Refunded, never two tickets for one seat.
        var seat = _h.Seats[2];
        var late = await _h.PlaceAsync([_h.Seat(seat)], MockOutcome.SucceededDelayed);
        var resold = await _h.PlaceAsync([_h.Seat(seat)]);
        (await _h.DeliverSucceededAsync(resold)).ShouldBe(PaymentWebhookOutcome.Paid);
        _h.Time.Advance(PastExpiry);

        await SweepAsync();

        var state = await _h.StateAsync(late.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.SeatsUnavailable);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(0);
        (await _h.StateAsync(resold.OrderId)).Tickets.ShouldBe(1);

        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);

        var refunded = await _h.StateAsync(late.OrderId);
        refunded.Status.ShouldBe(OrderStatus.Refunded);
        refunded.Outbox.ShouldBe([nameof(RefundRequested), nameof(OrderRefunded)]);
        _h.Spy.RefundKeys.ShouldBe([late.OrderId.ToString()]);
        (await _h.RefundedOnGatewayAsync(late.SessionId)).ShouldBe(late.Total.Amount);
        (await _h.WithDbAsync(db => db.Tickets.CountAsync(t => t.SeatIndex == seat && t.EventId == _h.EventId, Ct))).ShouldBe(1);
    }

    [Fact]
    public async Task A_webhook_that_pays_the_order_between_the_status_read_and_the_expire_commit_wins()
    {
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[3])], sessionOutlivesOrder: TimeSpan.FromMinutes(5));
        _h.Time.Advance(PastExpiry);
        var raced = 0;
        _h.Spy.AfterStatus = async (_, status) =>
        {
            status.ShouldBe(PaymentStatus.Pending);
            if (Interlocked.Increment(ref raced) == 1)
            {
                // The buyer pays right now and the webhook commits before the sweep's expiry does.
                (await _h.Control.CompleteAsync(placed.SessionId, MockOutcome.Succeeded, Ct)).IsSuccess.ShouldBeTrue();
                (await _h.DeliverSucceededAsync(placed)).ShouldBe(PaymentWebhookOutcome.Paid);
            }
        };
        using var metrics = _h.RecordMetrics();

        await SweepAsync();

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(1);
        state.Outbox.ShouldBe([nameof(OrderPaid)], "never Expired with money");
        metrics.Counted("ticketrush.orders.expiry").ShouldBe(["outcome=unchanged"]);
    }

    [Fact]
    public async Task A_gateway_outage_leaves_orders_that_may_be_paid_open_and_still_expires_the_others()
    {
        var first = await _h.PlaceAsync([_h.Seat(_h.Seats[4])]);
        var pending = await _h.PlacePendingAsync([_h.Seat(_h.Seats[5])]);
        var second = await _h.PlaceAsync([_h.Seat(_h.Seats[6])]);
        _h.Time.Advance(PastExpiry);
        _h.Gateway.SimulateOutage = true;
        using var metrics = _h.RecordMetrics();

        (await SweepAsync()).ShouldBe(3);

        (await _h.StateAsync(first.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await _h.StateAsync(second.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await _h.StateAsync(pending)).Status.ShouldBe(OrderStatus.Expired);
        _h.Spy.StatusCalls.ShouldBe(1, "after the first outage the pass does not ask the same gateway again");
        metrics.Counted("ticketrush.orders.expiry").ShouldBe(["outcome=skipped_outage", "outcome=expired", "outcome=skipped_outage"], ignoreOrder: true);

        _h.Gateway.SimulateOutage = false;
        await SweepAsync();

        (await _h.StateAsync(first.OrderId)).Status.ShouldBe(OrderStatus.Expired);
        (await _h.StateAsync(second.OrderId)).Status.ShouldBe(OrderStatus.Expired);
    }

    [Fact]
    public async Task A_pending_order_without_a_session_expires_without_asking_the_gateway()
    {
        var orderId = await _h.PlacePendingAsync([_h.GaUnits(2)]);
        _h.Time.Advance(PastExpiry);

        await SweepAsync();

        var state = await _h.StateAsync(orderId);
        state.Status.ShouldBe(OrderStatus.Expired);
        state.Outbox.ShouldBe([nameof(OrderExpired)]);
        _h.Spy.StatusCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Orders_not_yet_past_their_expiry_and_orders_that_are_no_longer_open_are_not_touched()
    {
        var young = await _h.PlaceAsync([_h.Seat(_h.Seats[7])], lifetime: TimeSpan.FromMinutes(20));
        var cancelled = await _h.PlacePendingAsync([_h.Seat(_h.Seats[8])]);
        await _h.ChangeOrderAsync(cancelled, o => o.Cancel());
        var paid = await _h.PlaceAsync([_h.Seat(_h.Seats[9])]);
        (await _h.DeliverSucceededAsync(paid)).ShouldBe(PaymentWebhookOutcome.Paid);
        _h.Time.Advance(PastExpiry);

        (await SweepAsync()).ShouldBe(0);

        (await _h.StateAsync(young.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await _h.StateAsync(cancelled)).Status.ShouldBe(OrderStatus.Cancelled);
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.Paid);
        _h.Spy.StatusCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_session_that_failed_in_our_records_but_succeeded_at_the_gateway_is_still_paid()
    {
        // Out-of-order provider: a Failed webhook was recorded, yet the gateway's final word is Succeeded.
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[10])], MockOutcome.SucceededDelayed);
        await _h.ChangeAttemptAsync(placed.AttemptId, a => a.MarkFailed());
        await _h.ChangeOrderAsync(placed.OrderId, o => o.MarkPaymentFailed());
        _h.Time.Advance(PastExpiry);

        await SweepAsync();

        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    public async Task Due_orders_are_listed_oldest_expiry_first_a_page_at_a_time_ties_included()
    {
        var third = await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(3));
        var tieA = await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(2));
        var tieB = await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(2));
        var first = await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(1));
        await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(30));
        var cancelled = await _h.PlacePendingAsync([_h.GaUnits(1)], TimeSpan.FromMinutes(1));
        await _h.ChangeOrderAsync(cancelled, o => o.Cancel());
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        var now = _h.Time.GetUtcNow();
        await using var scope = _h.Host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>();

        var page1 = await handler.ListDueAsync(now, null, 2, Ct);
        var page2 = await handler.ListDueAsync(now, page1.Next, 2, Ct);
        var page3 = await handler.ListDueAsync(now, page2.Next, 2, Ct);

        page1.OrderIds.Count.ShouldBe(2);
        page1.OrderIds[0].ShouldBe(first);
        page2.OrderIds.Count.ShouldBe(2);
        page2.OrderIds[1].ShouldBe(third);
        new[] { page1.OrderIds[1], page2.OrderIds[0] }.ShouldBe([tieA, tieB], ignoreOrder: true, "the tie is split across pages, neither lost nor repeated");
        page3.OrderIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_pass_works_through_more_than_one_page()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < OrderExpiryService.BatchSize + 3; i++)
        {
            ids.Add(await _h.PlacePendingAsync([_h.GaUnits(1)]));
        }

        _h.Time.Advance(PastExpiry);

        (await SweepAsync()).ShouldBe(ids.Count);

        (await _h.WithDbAsync(db => db.Orders.CountAsync(o => ids.Contains(o.Id) && o.Status == OrderStatus.Expired, Ct))).ShouldBe(ids.Count);
    }

    // ================================================================ several sessions, the session reconcile (B8 fix round 1)

    [Fact]
    public async Task Two_succeeded_sessions_pay_the_order_once_and_refund_the_second_as_a_duplicate()
    {
        var first = await _h.PlaceAsync([_h.Seat(_h.Seats[11])], MockOutcome.SucceededDelayed);
        var second = await _h.AddSessionAsync(first.OrderId);
        (await _h.Control.CompleteAsync(second.SessionId, MockOutcome.SucceededDelayed, Ct)).IsSuccess.ShouldBeTrue();
        _h.Time.Advance(PastExpiry);

        await SweepAsync();

        var state = await _h.StateAsync(first.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(1);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.RefundPending]);
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.AttemptAsync(second.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(second.SessionId)).ShouldBe(second.Total.Amount);
        (await _h.RefundedOnGatewayAsync(first.SessionId)).ShouldBe(0);
    }

    [Fact]
    public async Task A_pending_session_of_an_order_paid_by_another_one_is_closed_after_the_grace()
    {
        var paid = await _h.PlaceAsync([_h.Seat(_h.Seats[12])], MockOutcome.SucceededDelayed);
        var open = await _h.AddSessionAsync(paid.OrderId);
        _h.Time.Advance(PastExpiry);

        await SweepAsync();
        (await _h.StateAsync(paid.OrderId)).Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.Pending]);

        _h.Time.Advance(Grace);
        await SweepAsync();

        var state = await _h.StateAsync(paid.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.Expired]);
        (await _h.AttemptAsync(open.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Expired);
    }

    [Fact]
    public async Task A_second_fulfilment_that_throws_after_the_first_committed_is_finished_by_the_session_reconcile()
    {
        var first = await _h.PlaceAsync([_h.Seat(_h.Seats[13])], MockOutcome.SucceededDelayed);
        var second = await _h.AddSessionAsync(first.OrderId);
        (await _h.Control.CompleteAsync(second.SessionId, MockOutcome.SucceededDelayed, Ct)).IsSuccess.ShouldBeTrue();
        _h.Spy.FailInboxFor = ExpireOrderHandler.StatusEventId(second.SessionId);
        _h.Time.Advance(PastExpiry);
        using var metrics = _h.RecordMetrics();

        await SweepAsync();

        (await _h.StateAsync(first.OrderId)).Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.Pending]);
        metrics.Counted("ticketrush.orders.expiry").ShouldBe(["outcome=error"]);

        _h.Time.Advance(Grace);
        await SweepAsync();

        var state = await _h.StateAsync(first.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(1);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded, PaymentAttemptStatus.RefundPending], "the second payment goes back as a duplicate");
        metrics.Counted("ticketrush.payments.sessions_reconciled").ShouldBe(["outcome=refund_late"]);
    }

    [Fact]
    public async Task A_payment_completed_after_the_sweeps_pending_read_whose_webhook_never_arrives_is_found_later()
    {
        // The provider exhausted its webhook retries; only the session reconcile can find this money.
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[14])], sessionOutlivesOrder: TimeSpan.FromMinutes(5));
        _h.Time.Advance(PastExpiry);
        await SweepAsync();
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Expired);
        (await _h.Control.CompleteAsync(placed.SessionId, MockOutcome.SucceededDelayed, Ct)).IsSuccess.ShouldBeTrue();

        _h.Time.Advance(Grace);
        await SweepAsync();

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        state.Tickets.ShouldBe(1);
        state.Outbox.ShouldBe([nameof(OrderExpired), nameof(OrderPaid)]);
    }

    [Fact]
    public async Task Money_on_a_session_of_an_order_that_was_cancelled_meanwhile_is_sent_back()
    {
        // Safety net for B9: whatever closes an order, a session that took money is found and refunded.
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[15])], MockOutcome.SucceededDelayed);
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE orders SET status = 'Cancelled' WHERE id = {placed.OrderId}", Ct));
        _h.Time.Advance(PastExpiry + Grace);

        await SweepAsync();

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Cancelled);
        state.Attempts.ShouldBe([PaymentAttemptStatus.RefundPending]);
        await _h.Refunds().RunOnceAsync(Ct);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);
    }

    [Fact]
    public async Task A_gateway_outage_during_the_session_reconcile_marks_nothing()
    {
        var paid = await _h.PlaceAsync([_h.Seat(_h.Seats[16])], MockOutcome.SucceededDelayed);
        var open = await _h.AddSessionAsync(paid.OrderId);
        _h.Time.Advance(PastExpiry);
        await SweepAsync();
        _h.Time.Advance(Grace);
        _h.Gateway.SimulateOutage = true;

        await SweepAsync();

        (await _h.AttemptAsync(open.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Pending);
        _h.Gateway.SimulateOutage = false;
        await SweepAsync();
        (await _h.AttemptAsync(open.AttemptId)).Status.ShouldBe(PaymentAttemptStatus.Expired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_real_webhook_after_the_sweep_expired_the_order_is_paid_or_refunded_end_to_end(bool seatsFree)
    {
        var seat = _h.Seats[17];
        var late = await _h.PlaceAsync([_h.Seat(seat)], sessionOutlivesOrder: TimeSpan.FromMinutes(5));
        _h.Time.Advance(PastExpiry);
        await SweepAsync();
        (await _h.StateAsync(late.OrderId)).Attempts.ShouldBe([PaymentAttemptStatus.Pending]);
        if (!seatsFree)
        {
            var resold = await _h.PlaceAsync([_h.Seat(seat)]);
            (await _h.DeliverSucceededAsync(resold)).ShouldBe(PaymentWebhookOutcome.Paid);
        }

        (await _h.Control.CompleteAsync(late.SessionId, MockOutcome.Succeeded, Ct)).IsSuccess.ShouldBeTrue();
        (await _h.PostWebhookAsync(late, late.Total.Amount)).ShouldBe(seatsFree ? "paid" : "refund_pending");

        if (seatsFree)
        {
            (await _h.StateAsync(late.OrderId)).Tickets.ShouldBe(1);
            return;
        }

        (await _h.StateAsync(late.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        await _h.Refunds().RunOnceAsync(Ct);
        (await _h.StateAsync(late.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(late.SessionId)).ShouldBe(late.Total.Amount);
    }

    [Fact]
    public async Task A_real_webhook_reporting_another_amount_than_the_status_decision_assumed_is_flagged_not_silently_ignored()
    {
        // Status reads carry no captured amount: the sweep pays with the session amount; the real webhook says otherwise.
        var placed = await _h.PlaceAsync([_h.GaUnits(2)], MockOutcome.SucceededDelayed);
        _h.Time.Advance(PastExpiry);
        await SweepAsync();
        var before = await _h.StateAsync(placed.OrderId);
        before.Status.ShouldBe(OrderStatus.Paid);

        (await _h.PostWebhookAsync(placed, placed.Total.Amount - 1_000)).ShouldBe("amount_mismatch_after_status");
        (await _h.PostWebhookAsync(placed, placed.Total.Amount, "USD")).ShouldBe("amount_mismatch_after_status");
        (await _h.PostWebhookAsync(placed, placed.Total.Amount)).ShouldBe("ignored", "the same amount confirms the decision");

        var after = await _h.StateAsync(placed.OrderId);
        after.Status.ShouldBe(before.Status);
        after.Tickets.ShouldBe(before.Tickets);
        after.Attempts.ShouldBe(before.Attempts);
    }

    [Fact]
    public async Task A_second_webhook_with_another_amount_after_a_real_webhook_decided_is_still_just_ignored()
    {
        var placed = await _h.PlaceAsync([_h.GaUnits(1)], MockOutcome.Succeeded);
        (await _h.PostWebhookAsync(placed, placed.Total.Amount)).ShouldBe("paid");

        (await _h.PostWebhookAsync(placed, placed.Total.Amount - 1_000)).ShouldBe("ignored");
    }

    [Fact]
    public async Task An_attempt_of_an_unknown_provider_never_expires_its_order_and_is_counted_as_an_error()
    {
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[18])]);
        await _h.WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE payment_attempts SET provider = 'ghost' WHERE id = {placed.AttemptId}", Ct));
        _h.Time.Advance(PastExpiry);
        using var metrics = _h.RecordMetrics();

        await SweepAsync();

        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        metrics.Counted("ticketrush.orders.expiry").ShouldBe(["outcome=error"]);
    }

    [Fact]
    public async Task Two_sweepers_expiring_the_same_order_at_once_expire_it_once()
    {
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[19])], sessionOutlivesOrder: TimeSpan.FromMinutes(5));
        _h.Time.Advance(PastExpiry);
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _h.Spy.AfterStatus = async (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }

            await both.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        };
        using var metrics = _h.RecordMetrics();

        await Task.WhenAll(_h.Sweeper().RunOnceAsync(Ct), _h.Sweeper().RunOnceAsync(Ct));

        (await _h.StateAsync(placed.OrderId)).Outbox.ShouldBe([nameof(OrderExpired)]);
        metrics.Counted("ticketrush.orders.expiry").ShouldBe(["outcome=expired", "outcome=unchanged"], ignoreOrder: true);
    }

    [Fact]
    public async Task Only_the_leader_sweeps()
    {
        using var first = _h.Sweeper(TimeProvider.System);
        using var second = _h.Sweeper(TimeProvider.System);
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
