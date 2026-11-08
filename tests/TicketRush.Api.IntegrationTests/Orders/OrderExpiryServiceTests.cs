extern alias WorkerAssembly;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Orders;
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

    private CheckoutWorkersHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await CheckoutWorkersHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    private async Task<int> SweepAsync() => await _h.Sweeper().RunOnceAsync(Ct);

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("expired")]
    public async Task An_order_whose_session_took_no_money_expires_with_its_pending_session(string gatewayState)
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
        state.Attempts.ShouldBe([PaymentAttemptStatus.Expired]);
        state.Tickets.ShouldBe(0);
        state.Outbox.ShouldBe([nameof(OrderExpired)]);
        _h.Spy.StatusCalls.ShouldBe(1);
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
