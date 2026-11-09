using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Features.Organizer;
using TicketRush.Application.Features.Organizer.Events;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Tickets;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Redis;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Orders;

/// <summary>
/// Spec 08 "EventCancelled handler" (task B9) against Postgres, Redis, the real mock gateway, the real fulfil path, the expiry
/// sweep and the refund worker: every Paid order of a cancelled event goes to RefundPending in the SAME transaction that
/// voids its tickets and gives its general-admission units back; open orders never end Cancelled while they may hold money;
/// the organizer's cancel and in-flight fulfilments are ordered by the event sale lock.
/// </summary>
public sealed class EventCancelledOrdersTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(15);

    private CheckoutWorkersHarness _h = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _h = await CheckoutWorkersHarness.CreateAsync(containers);

    public async ValueTask DisposeAsync() => await _h.DisposeAsync();

    // ================================================================ helpers

    private async Task<CheckoutWorkersHarness.Placed> PaidAsync(params OrderItem[] items)
    {
        var placed = await _h.PlaceAsync(items, MockOutcome.SucceededDelayed);
        (await _h.DeliverSucceededAsync(placed)).ShouldBe(PaymentWebhookOutcome.Paid);
        return placed;
    }

    private Task<Guid> OrganizerAsync() =>
        _h.WithDbAsync(db => db.Events.Where(e => e.Id == _h.EventId).Select(e => e.OrganizerId).SingleAsync(Ct));

    /// <summary>The organizer's cancel (<c>POST /organizer/events/{id}/cancel</c>'s use case).</summary>
    private async Task CancelEventAsync()
    {
        var organizer = await OrganizerAsync();
        await using var scope = _h.Host.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<CancelEventHandler>().HandleAsync(_h.EventId, organizer, Ct);
        result.IsSuccess.ShouldBeTrue();
    }

    /// <summary>One delivery of the <see cref="EventCancelled"/> message to its outbox consumer.</summary>
    private async Task DeliverEventCancelledAsync()
    {
        var organizer = await OrganizerAsync();
        await using var scope = _h.Host.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<EventCancelledInventoryHandler>();
        await handler.HandleAsync(new EventCancelled(_h.EventId, organizer), new IntegrationEventContext(Guid.NewGuid(), _h.Time.GetUtcNow(), 1), Ct);
    }

    /// <summary>Runs the real outbox until nothing is due (what the worker does).</summary>
    private async Task DrainOutboxAsync()
    {
        var processor = _h.Host.Services.GetRequiredService<OutboxProcessor>();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    private Task<EventStatus> EventStatusAsync() =>
        _h.WithDbAsync(db => db.Events.AsNoTracking().Where(e => e.Id == _h.EventId).Select(e => e.Status).SingleAsync(Ct));

    private Task<List<TicketStatus>> TicketsAsync(Guid orderId) =>
        _h.WithDbAsync(db => db.Tickets.AsNoTracking().Where(t => t.OrderId == orderId).Select(t => t.Status).ToListAsync(Ct));

    private Task<int> SoldCountAsync() => _h.WithDbAsync(db =>
        db.Database.SqlQuery<int>($"SELECT sold_count AS \"Value\" FROM ticket_tiers WHERE id = {_h.Ga.Id}").SingleAsync(Ct));

    private Task<int> LiveTicketsOfEventAsync() =>
        _h.WithDbAsync(db => db.Tickets.CountAsync(t => t.EventId == _h.EventId && t.Status != TicketStatus.Voided, Ct));

    /// <summary>C1 plus B2's invariant: one live ticket per seat, and every GA tier's sold_count = its non-voided GA tickets.</summary>
    private Task<int> AssertInventoryInvariantsAsync() => _h.WithDbAsync(async db =>
    {
        var doubleSold = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM (
              SELECT event_id, seat_index FROM tickets
              WHERE status <> 'Voided' AND seat_index IS NOT NULL
              GROUP BY event_id, seat_index HAVING count(*) > 1) d
            """).SingleAsync(Ct);
        var badTiers = await db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM ticket_tiers tt
            WHERE tt.capacity IS NOT NULL AND (tt.sold_count < 0 OR tt.sold_count > tt.capacity OR tt.sold_count <> (
              SELECT count(*) FROM tickets t WHERE t.tier_id = tt.id AND t.seat_index IS NULL AND t.status <> 'Voided'))
            """).SingleAsync(Ct);
        doubleSold.ShouldBe(0, "a seat has more than one non-voided ticket");
        badTiers.ShouldBe(0, "a tier's sold_count is negative, over capacity or differs from its GA tickets");
        return 0;
    });

    /// <summary>Waits until some transaction of this test's database queues for an advisory lock (the sale lock).</summary>
    private async Task WaitForSaleLockWaiterAsync()
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (await _h.WithDbAsync(db => db.Database.SqlQuery<int>($"""
            SELECT count(*)::int AS "Value" FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """).SingleAsync(Ct)) == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "nobody queued for the event sale lock");
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>Pauses the first transaction granted the sale lock in the given mode until <c>release</c> completes.</summary>
    private (TaskCompletionSource Holding, TaskCompletionSource Release) PauseFirst(bool exclusive)
    {
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        _h.SaleLocks.AfterAcquire = async isExclusive =>
        {
            if (isExclusive == exclusive && Interlocked.Exchange(ref paused, 1) == 0)
            {
                holding.SetResult();
                await release.Task;
            }
        };
        return (holding, release);
    }

    /// <summary>A refund call for an order whose tickets are not all voided is a bug: recorded and asserted on at the end.</summary>
    private List<Guid> WatchRefundsForLiveTickets()
    {
        var violations = new List<Guid>();
        _h.Spy.BeforeRefund = async key =>
        {
            if (Guid.TryParse(key, out var orderId) && (await TicketsAsync(orderId)).Any(s => s != TicketStatus.Voided))
            {
                lock (violations)
                {
                    violations.Add(orderId);
                }
            }
        };
        return violations;
    }

    private async Task<(OrderStatus Status, string? Reason, DateTimeOffset UpdatedAt)> SnapshotAsync(Guid orderId) =>
        await _h.WithDbAsync(db => db.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new ValueTuple<OrderStatus, string?, DateTimeOffset>(o.Status, o.RefundReason, o.UpdatedAt)).SingleAsync(Ct));

    /// <summary><paramref name="count"/> Paid orders of one GA unit each, written straight to the database (fast), sold_count included.</summary>
    private Task<int> BulkPaidGaOrdersAsync(int count) => _h.WithDbAsync(async db =>
    {
        var now = _h.Time.GetUtcNow();
        for (var i = 0; i < count; i++)
        {
            var order = Order.Create(Guid.CreateVersion7(), _h.EventId, Guid.CreateVersion7(), [_h.GaUnits(1)], new Money(_h.Ga.Price),
                new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now + TimeSpan.FromMinutes(8), now);
            order.StartPayment(now);
            db.Tickets.AddRange(order.IssueTickets());
            order.MarkPaid(now);
            db.Orders.Add(order);
        }

        await db.SaveChangesAsync(Ct);
        return await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE ticket_tiers SET sold_count = sold_count + {count} WHERE id = {_h.Ga.Id}", Ct);
    });

    private Task<int> CountOrdersAsync(OrderStatus status) => _h.WithDbAsync(db => db.Orders.CountAsync(o => o.EventId == _h.EventId && o.Status == status, Ct));

    // ================================================================ paid orders

    [Fact]
    public async Task Paid_seated_and_ga_orders_are_voided_released_and_refund_pending_then_refunded_after_the_void()
    {
        var seated = await PaidAsync(_h.Seat(_h.Seats[0]), _h.Seat(_h.Seats[1]));
        var ga = await PaidAsync(_h.GaUnits(3));
        (await SoldCountAsync()).ShouldBe(3);
        var violations = WatchRefundsForLiveTickets();
        using var metrics = _h.RecordMetrics();

        await CancelEventAsync();
        await DrainOutboxAsync();

        foreach (var placed in new[] { seated, ga })
        {
            var state = await _h.StateAsync(placed.OrderId);
            state.Status.ShouldBe(OrderStatus.RefundPending);
            state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
            state.Outbox.Count(t => t == nameof(RefundRequested)).ShouldBe(1);
            (await TicketsAsync(placed.OrderId)).ShouldAllBe(s => s == TicketStatus.Voided);
        }

        (await TicketsAsync(seated.OrderId)).Count.ShouldBe(2);
        (await TicketsAsync(ga.OrderId)).Count.ShouldBe(3);
        (await SoldCountAsync()).ShouldBe(0);
        (await LiveTicketsOfEventAsync()).ShouldBe(0);
        await AssertInventoryInvariantsAsync();
        (await _h.WithDbAsync(db => db.OutboxMessages.Where(m => m.Type == nameof(EventCancelled) && m.AggregateId == _h.EventId).Select(m => m.ProcessedAt).SingleAsync(Ct))).ShouldNotBeNull();
        metrics.Total("ticketrush.orders.event_cancelled", "outcome=refund_pending").ShouldBe(2);
        metrics.Total("ticketrush.orders.event_cancelled", "outcome=cancelled").ShouldBe(0);
        metrics.Total("ticketrush.tickets.voided", "reason=event_cancelled").ShouldBe(5);

        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(2);

        (await _h.StateAsync(seated.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.StateAsync(ga.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(seated.SessionId)).ShouldBe(seated.Total.Amount);
        (await _h.RefundedOnGatewayAsync(ga.SessionId)).ShouldBe(ga.Total.Amount);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Twelve_hundred_paid_orders_settle_in_batches_of_500_and_a_run_that_fails_midway_resumes_without_double_work()
    {
        await BulkPaidGaOrdersAsync(1200);
        (await SoldCountAsync()).ShouldBe(1200);
        await CancelEventAsync();
        var batches = new List<EventOrderSettlement>();

        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            var failing = new FailingTierInventory(scope.ServiceProvider.GetRequiredService<ITierInventory>(), failOnCall: 2);
            var handler = ActivatorUtilities.CreateInstance<CancelEventOrdersHandler>(scope.ServiceProvider, (ITierInventory)failing);
            await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(_h.EventId, batches.Add, Ct));
        }

        // The first batch committed whole, the second rolled back whole: never a RefundPending order with live tickets.
        batches.Select(b => b.RefundPending).ShouldBe([CancelEventOrdersHandler.BatchSize]);
        (await CountOrdersAsync(OrderStatus.RefundPending)).ShouldBe(500);
        (await CountOrdersAsync(OrderStatus.Paid)).ShouldBe(700);
        (await SoldCountAsync()).ShouldBe(700);
        (await LiveTicketsOfEventAsync()).ShouldBe(700);
        await AssertInventoryInvariantsAsync();

        batches.Clear();
        await DeliverEventCancelledAsync();

        (await CountOrdersAsync(OrderStatus.RefundPending)).ShouldBe(1200);
        (await SoldCountAsync()).ShouldBe(0);
        (await LiveTicketsOfEventAsync()).ShouldBe(0);
        (await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(1200);
        await AssertInventoryInvariantsAsync();

        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>();
            (await handler.HandleAsync(_h.EventId, batches.Add, Ct)).ShouldBe(EventOrderSettlement.None);
        }

        batches.ShouldBeEmpty();
        (await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(1200);
        (await SoldCountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_full_run_commits_batches_of_at_most_500_orders()
    {
        await BulkPaidGaOrdersAsync(1200);
        await CancelEventAsync();
        var batches = new List<EventOrderSettlement>();

        await using var scope = _h.Host.Services.CreateAsyncScope();
        var total = await scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>().HandleAsync(_h.EventId, batches.Add, Ct);

        batches.Select(b => b.Orders).ShouldBe([500, 500, 200]);
        total.ShouldBe(new EventOrderSettlement(0, 1200, 1200));
    }

    [Fact]
    public async Task A_checked_in_ticket_is_voided_too_and_its_ga_unit_released()
    {
        var paid = await PaidAsync(_h.GaUnits(2));
        await _h.WithDbAsync(async db =>
        {
            (await db.Tickets.FirstAsync(t => t.OrderId == paid.OrderId, Ct)).CheckIn();
            return await db.SaveChangesAsync(Ct);
        });

        await CancelEventAsync();
        await DeliverEventCancelledAsync();

        (await TicketsAsync(paid.OrderId)).ShouldBe([TicketStatus.Voided, TicketStatus.Voided]);
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        (await SoldCountAsync()).ShouldBe(0);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task Orders_already_refund_pending_refunded_expired_or_cancelled_are_left_untouched()
    {
        var refunded = await _h.PlaceAsync([_h.GaUnits(1)], MockOutcome.SucceededDelayed);
        await _h.ChangeOrderAsync(refunded.OrderId, o => o.MarkUnfulfillable());
        await _h.ChangeAttemptAsync(refunded.AttemptId, a => a.MarkSucceeded());
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(refunded.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        var unfulfillable = await _h.PlaceAsync([_h.GaUnits(1)], MockOutcome.SucceededDelayed);
        await _h.ChangeOrderAsync(unfulfillable.OrderId, o => o.MarkUnfulfillable());
        await _h.ChangeAttemptAsync(unfulfillable.AttemptId, a => a.MarkSucceeded());
        var expired = await _h.PlacePendingAsync([_h.GaUnits(1)]);
        await _h.ChangeOrderAsync(expired, o => o.Expire());
        var cancelled = await _h.PlacePendingAsync([_h.GaUnits(1)]);
        await _h.ChangeOrderAsync(cancelled, o => o.Cancel());
        var ids = new[] { unfulfillable.OrderId, refunded.OrderId, expired, cancelled };
        var before = new List<(OrderStatus, string?, DateTimeOffset)>();
        foreach (var id in ids)
        {
            before.Add(await SnapshotAsync(id));
        }

        var refundRequests = await _h.OutboxCountAsync(nameof(RefundRequested));

        await CancelEventAsync();
        await DeliverEventCancelledAsync();

        for (var i = 0; i < ids.Length; i++)
        {
            (await SnapshotAsync(ids[i])).ShouldBe(before[i]);
        }

        (await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(refundRequests);
    }

    // ================================================================ open orders

    [Fact]
    public async Task Pending_orders_are_cancelled_and_orders_that_may_hold_money_stay_open_for_the_expiry_sweep()
    {
        var pending = await _h.PlacePendingAsync([_h.GaUnits(2)]);
        var captured = await _h.PlaceAsync([_h.Seat(_h.Seats[0])], MockOutcome.SucceededDelayed); // paid at the gateway, webhook lost
        var failed = await _h.PlaceAsync([_h.GaUnits(1)]);
        (await _h.DeliverFailedAsync(failed)).ShouldBe(PaymentWebhookOutcome.Failed); // the real Failed-webhook path
        var unpaid = await _h.PlaceAsync([_h.GaUnits(1)]);
        using var metrics = _h.RecordMetrics();

        await CancelEventAsync();
        await DeliverEventCancelledAsync();

        (await _h.StateAsync(pending)).Status.ShouldBe(OrderStatus.Cancelled);
        (await _h.StateAsync(captured.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        (await _h.StateAsync(unpaid.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
        var failedState = await _h.StateAsync(failed.OrderId);
        failedState.Status.ShouldBe(OrderStatus.PaymentFailed);
        failedState.Attempts.ShouldBe([PaymentAttemptStatus.Failed]);
        metrics.Total("ticketrush.orders.event_cancelled", "outcome=cancelled").ShouldBe(1);
        metrics.Total("ticketrush.orders.event_cancelled", "outcome=refund_pending").ShouldBe(0);

        // The normal expiry sweep (B8) asks every session at ExpiresAt: the captured money is sent back, the rest expire.
        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _h.Sweeper().RunOnceAsync(Ct);

        var refund = await _h.StateAsync(captured.OrderId);
        refund.Status.ShouldBe(OrderStatus.RefundPending);
        refund.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        refund.Tickets.ShouldBe(0);
        refund.Attempts.ShouldBe([PaymentAttemptStatus.Succeeded]);
        (await _h.StateAsync(unpaid.OrderId)).Status.ShouldBe(OrderStatus.Expired);
        (await _h.StateAsync(failed.OrderId)).Status.ShouldBe(OrderStatus.Expired);

        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(captured.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(captured.SessionId)).ShouldBe(captured.Total.Amount);
        (await _h.WithDbAsync(db => db.Orders.CountAsync(o => o.Status == OrderStatus.Cancelled && db.PaymentAttempts.Any(a => a.OrderId == o.Id), Ct))).ShouldBe(0);
        await AssertInventoryInvariantsAsync();
    }

    [Fact]
    public async Task A_session_reported_failed_that_then_captured_money_is_found_and_refunded_by_the_normal_expiry_sweep()
    {
        // Out-of-order provider: the session captured the money, but a Failed event was delivered and the success webhook is lost.
        var placed = await _h.PlaceAsync([_h.GaUnits(2)], MockOutcome.SucceededDelayed);
        (await _h.DeliverFailedAsync(placed)).ShouldBe(PaymentWebhookOutcome.Failed);
        await CancelEventAsync();
        await DeliverEventCancelledAsync();
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.PaymentFailed);

        _h.Time.Advance(TimeSpan.FromMinutes(10));
        await _h.Sweeper().RunOnceAsync(Ct);

        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        state.Tickets.ShouldBe(0);
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        (await _h.RefundedOnGatewayAsync(placed.SessionId)).ShouldBe(placed.Total.Amount);
    }

    [Fact]
    public async Task A_webhook_arriving_after_the_sweep_is_refunded_as_event_cancelled_and_issues_nothing()
    {
        var late = await _h.PlaceAsync([_h.GaUnits(2)], MockOutcome.SucceededDelayed);
        await CancelEventAsync();
        await DeliverEventCancelledAsync();
        (await _h.StateAsync(late.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);

        (await _h.DeliverSucceededAsync(late)).ShouldBe(PaymentWebhookOutcome.RefundPending);

        var state = await _h.StateAsync(late.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        state.Tickets.ShouldBe(0);
        (await SoldCountAsync()).ShouldBe(0);
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(late.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
    }

    // ================================================================ races

    [Fact]
    public async Task The_organizer_cancel_waits_for_a_fulfilment_in_flight_and_the_sweep_then_refunds_that_order()
    {
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[0]), _h.GaUnits(1)], MockOutcome.SucceededDelayed);
        var (holding, release) = PauseFirst(exclusive: false);
        var fulfil = Task.Run(() => _h.DeliverSucceededAsync(placed), Ct);
        await holding.Task.WaitAsync(LockWait, Ct);

        var cancel = Task.Run(CancelEventAsync, Ct);
        await WaitForSaleLockWaiterAsync();
        cancel.IsCompleted.ShouldBeFalse();
        (await EventStatusAsync()).ShouldBe(EventStatus.Published);
        release.SetResult();

        (await fulfil).ShouldBe(PaymentWebhookOutcome.Paid);
        await cancel;
        (await EventStatusAsync()).ShouldBe(EventStatus.Cancelled);

        var violations = WatchRefundsForLiveTickets();
        await DrainOutboxAsync();
        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        (await TicketsAsync(placed.OrderId)).ShouldBe([TicketStatus.Voided, TicketStatus.Voided]);
        await AssertInventoryInvariantsAsync();
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_fulfilment_that_arrives_while_the_cancel_holds_the_lock_waits_then_refunds_as_event_cancelled()
    {
        var placed = await _h.PlaceAsync([_h.Seat(_h.Seats[0]), _h.GaUnits(1)], MockOutcome.SucceededDelayed);
        var (holding, release) = PauseFirst(exclusive: true);
        var cancel = Task.Run(CancelEventAsync, Ct);
        await holding.Task.WaitAsync(LockWait, Ct);

        var fulfil = Task.Run(() => _h.DeliverSucceededAsync(placed), Ct);
        await WaitForSaleLockWaiterAsync();
        fulfil.IsCompleted.ShouldBeFalse();
        release.SetResult();

        await cancel;
        (await fulfil).ShouldBe(PaymentWebhookOutcome.RefundPending);
        var state = await _h.StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.RefundPending);
        state.RefundReason.ShouldBe(RefundReasons.EventCancelled);
        state.Tickets.ShouldBe(0);

        await DrainOutboxAsync();
        (await _h.StateAsync(placed.OrderId)).Outbox.Count(t => t == nameof(RefundRequested)).ShouldBe(1);
        (await SoldCountAsync()).ShouldBe(0);
        await AssertInventoryInvariantsAsync();
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
    }

    [Fact]
    public async Task The_refund_worker_running_during_the_sweep_never_refunds_an_order_with_a_live_ticket()
    {
        var orders = new List<CheckoutWorkersHarness.Placed>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await PaidAsync(i % 2 == 0 ? _h.Seat(_h.Seats[i]) : _h.GaUnits(2)));
        }

        var violations = WatchRefundsForLiveTickets();
        await CancelEventAsync();

        var sweep = Task.Run(DeliverEventCancelledAsync, Ct);
        var worker = Task.Run(
            async () =>
            {
                while (!sweep.IsCompleted)
                {
                    await _h.Refunds().RunOnceAsync(Ct);
                }
            },
            Ct);
        await sweep;
        await worker;
        await _h.Refunds().RunOnceAsync(Ct);

        foreach (var placed in orders)
        {
            (await _h.StateAsync(placed.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        }

        violations.ShouldBeEmpty();
        _h.Spy.RefundKeys.Distinct().Count().ShouldBe(6);
        _h.Spy.RefundKeys.Count.ShouldBe(6);
        await AssertInventoryInvariantsAsync();
    }

    // ================================================================ delivery

    [Fact]
    public async Task A_room_that_cannot_be_closed_does_not_stop_the_order_settlement_and_the_retry_closes_it()
    {
        var paid = await PaidAsync(_h.GaUnits(1));
        var pending = await _h.PlacePendingAsync([_h.GaUnits(1)]);
        var keys = _h.Host.Services.GetRequiredService<RedisKeys>();
        var redis = _h.Host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        var phase = await _h.WithDbAsync(db => db.Events.Where(e => e.Id == _h.EventId).SelectMany(e => e.Phases).Select(p => p.Id).FirstAsync(Ct));
        await redis.KeyDeleteAsync(keys.QueueState(phase));
        await redis.StringSetAsync(keys.QueueState(phase), "not-a-hash"); // the status script fails with WRONGTYPE
        await CancelEventAsync();

        // Settling needs only the database and the sale lock: done now, while the room failure is still reported for the retry.
        var failure = await Should.ThrowAsync<AggregateException>(DeliverEventCancelledAsync);

        failure.InnerExceptions.ShouldNotBeEmpty();
        failure.InnerExceptions.ShouldAllBe(e => e is RedisException || e is TimeoutException);
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        (await _h.StateAsync(pending)).Status.ShouldBe(OrderStatus.Cancelled);
        (await SoldCountAsync()).ShouldBe(0);

        await redis.KeyDeleteAsync(keys.QueueState(phase));
        await redis.HashSetAsync(keys.QueueState(phase), "status", "Open"); // the outage is over, the room still open
        await DeliverEventCancelledAsync();

        ((string?)await redis.HashGetAsync(keys.QueueState(phase), "status")).ShouldBe("Closed");
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        (await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(1);
    }

    [Fact]
    public async Task Delivering_the_message_twice_changes_nothing_the_second_time()
    {
        var paid = await PaidAsync(_h.Seat(_h.Seats[0]), _h.GaUnits(2));
        var pending = await _h.PlacePendingAsync([_h.GaUnits(1)]);
        await CancelEventAsync();
        await DeliverEventCancelledAsync();
        var first = (await SnapshotAsync(paid.OrderId), await SnapshotAsync(pending), await SoldCountAsync(), await _h.OutboxCountAsync(nameof(RefundRequested)));

        await DeliverEventCancelledAsync();

        (await SnapshotAsync(paid.OrderId), await SnapshotAsync(pending), await SoldCountAsync(), await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(first);
        first.Item1.Status.ShouldBe(OrderStatus.RefundPending);
        first.Item3.ShouldBe(0);
        first.Item4.ShouldBe(1);
    }

    [Fact]
    public async Task The_settlement_does_nothing_for_an_event_that_is_not_cancelled()
    {
        var paid = await PaidAsync(_h.GaUnits(1));

        await using var scope = _h.Host.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<CancelEventOrdersHandler>().HandleAsync(_h.EventId, null, Ct)).ShouldBe(EventOrderSettlement.None);

        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.Paid);
        (await SoldCountAsync()).ShouldBe(1);
    }

    // ================================================================ backstop sweep (dead-lettered message)

    [Fact]
    public async Task A_cancelled_event_whose_message_never_ran_is_settled_by_the_backstop_sweep_once()
    {
        var paid = await PaidAsync(_h.Seat(_h.Seats[0]), _h.GaUnits(2));
        var pending = await _h.PlacePendingAsync([_h.GaUnits(1)]);
        await CancelEventAsync(); // its EventCancelled message is never delivered (dead-lettered)
        var violations = WatchRefundsForLiveTickets();
        using var metrics = _h.RecordMetrics();

        (await _h.CancelledEvents().RunOnceAsync(Ct)).ShouldBe(1);

        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.RefundPending);
        (await TicketsAsync(paid.OrderId)).ShouldAllBe(s => s == TicketStatus.Voided);
        (await _h.StateAsync(pending)).Status.ShouldBe(OrderStatus.Cancelled);
        (await SoldCountAsync()).ShouldBe(0);
        await AssertInventoryInvariantsAsync();
        metrics.Total("ticketrush.orders.cancelled_event_sweep", "outcome=settled").ShouldBe(1);
        metrics.Total("ticketrush.orders.event_cancelled", "outcome=refund_pending").ShouldBe(1);

        (await _h.CancelledEvents().RunOnceAsync(Ct)).ShouldBe(0);
        (await _h.OutboxCountAsync(nameof(RefundRequested))).ShouldBe(1);
        (await _h.Refunds().RunOnceAsync(Ct)).ShouldBe(1);
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.Refunded);
        violations.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_backstop_sweep_costs_one_query_when_no_cancelled_event_has_orders_left_to_settle()
    {
        var paid = await PaidAsync(_h.GaUnits(1)); // a Published event's order; the seed also has cancelled events without orders
        _h.Queries.Reset();

        (await _h.CancelledEvents().RunOnceAsync(Ct)).ShouldBe(0);

        _h.Queries.Matching(_ => true).Count.ShouldBe(1);
        (await _h.StateAsync(paid.OrderId)).Status.ShouldBe(OrderStatus.Paid);
    }

    // ================================================================ organizer cancel under contention

    [Fact]
    public async Task An_organizer_cancel_that_waits_past_the_lock_timeout_is_a_409_try_again_and_changes_nothing()
    {
        var placed = await _h.PlaceAsync([_h.GaUnits(1)], MockOutcome.SucceededDelayed);
        var cancelledBefore = await _h.OutboxCountAsync(nameof(EventCancelled));
        var (holding, release) = PauseFirst(exclusive: false);
        var fulfil = Task.Run(() => _h.DeliverSucceededAsync(placed), Ct);
        await holding.Task.WaitAsync(LockWait, Ct);

        Result<OrganizerEvent> result;
        await using (var scope = _h.Host.Services.CreateAsyncScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<CancelEventHandler>().HandleAsync(_h.EventId, await OrganizerAsync(), Ct);
        }

        release.SetResult();
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.ShouldBe(EventErrors.CancelBusy.Code);
        result.Error.Type.ShouldBe(ErrorType.Conflict);
        (await EventStatusAsync()).ShouldBe(EventStatus.Published);
        (await fulfil).ShouldBe(PaymentWebhookOutcome.Paid);
        (await _h.OutboxCountAsync(nameof(EventCancelled))).ShouldBe(cancelledBefore);

        await CancelEventAsync(); // the organizer's retry
        (await EventStatusAsync()).ShouldBe(EventStatus.Cancelled);
    }

    /// <summary>The real tier inventory, except that its <c>failOnCall</c>-th release throws (a crash mid-run).</summary>
    private sealed class FailingTierInventory(ITierInventory inner, int failOnCall) : ITierInventory
    {
        private int _releases;

        public Task<bool> TryClaimAsync(Guid tierId, int quantity, CancellationToken ct) => inner.TryClaimAsync(tierId, quantity, ct);

        public Task<bool> ReleaseAsync(Guid tierId, int quantity, CancellationToken ct) =>
            Interlocked.Increment(ref _releases) == failOnCall
                ? throw new InvalidOperationException("simulated crash in the middle of the settlement")
                : inner.ReleaseAsync(tierId, quantity, ct);
    }
}
