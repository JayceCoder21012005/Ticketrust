using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Features.Notifications;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Inventory;
using TicketRush.Infrastructure.Queue;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Remembers what the handlers pushed to users' open tabs; with <see cref="Throws"/> every push fails like a dead hub.</summary>
public sealed class RecordingUserNotifier : IUserNotifier
{
    public ConcurrentQueue<(Guid UserId, NotificationItem Item)> Items { get; } = new();

    public ConcurrentQueue<(Guid UserId, Guid OrderId, string Status)> Statuses { get; } = new();

    public bool Throws { get; set; }

    /// <summary>When set, the next push cancels this source first and then fails like an aborted call.</summary>
    public CancellationTokenSource? CancelOnPush { get; set; }

    /// <summary>Every push call, including the ones that threw.</summary>
    public int Calls => _calls;

    private int _calls;

    public Task PushAsync(Guid userId, NotificationItem item, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        CancelOnPush?.Cancel();
        ct.ThrowIfCancellationRequested();
        Fail();
        Items.Enqueue((userId, item));
        return Task.CompletedTask;
    }

    public Task PushOrderStatusAsync(Guid userId, Guid orderId, string status, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        Fail();
        Statuses.Enqueue((userId, orderId, status));
        return Task.CompletedTask;
    }

    private void Fail()
    {
        if (Throws)
        {
            throw new InvalidOperationException("hub down");
        }
    }
}

/// <summary>
/// The order, refund and expiry notices (spec 09): real outbox, real handlers, real writer and delivery processor on a fake
/// clock. Orders raise their events through the aggregate, so the outbox rows are the ones production writes.
/// </summary>
public sealed class OrderNotificationTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string EventSlug = "son-tung-mtp-live";

    private readonly FakeTimeProvider _clock = new(NotificationTestHost.Start);
    private readonly ScriptedEmailSender _sender = new();
    private readonly RecordingUserNotifier _notifier = new();
    private NotificationTestHost _host = null!;
    private Guid _eventId;
    private string _eventTitle = string.Empty;
    private Guid _tierId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new NotificationTestHost(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, _sender, _clock, notifier: _notifier);
        await using var scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = NotificationTestHost.Db(scope);
        var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Slug == EventSlug, Ct);
        _eventId = evt.Id;
        _eventTitle = evt.Title;
        _tierId = evt.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission).Id;
        await scope.ServiceProvider.GetRequiredService<InventorySeeder>().SeedAsync(_eventId, Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // ---------------------------------------------------------------- fixtures

    /// <summary>A one-line order of 1.250.000 ₫ owned by <paramref name="userId"/>, moved on by <paramref name="advance"/> and saved (raising the events).</summary>
    private async Task<Guid> OrderAsync(Guid userId, Action<Order> advance)
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var now = _clock.GetUtcNow();
        var items = new[] { new OrderItem(_tierId, null, new Money(1_250_000), 1) };
        var order = Order.Create(userId, _eventId, Guid.CreateVersion7(), items, new Money(1_250_000), new BuyerInfo("Nguyễn Văn Đạt", "dat@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        advance(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.Id;
    }

    private Task<Guid> PaidOrderAsync(Guid userId) => OrderAsync(userId, o => o.MarkPaid(_clock.GetUtcNow()));

    private async Task<List<Notification>> NotificationsAsync()
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).Notifications.AsNoTracking().OrderBy(n => n.CreatedAt).ThenBy(n => n.DedupeKey).ToListAsync(Ct);
    }

    private async Task<List<EmailDelivery>> DeliveriesAsync()
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).EmailDeliveries.AsNoTracking().OrderBy(d => d.DedupeKey).ToListAsync(Ct);
    }

    private async Task AddOutboxAsync(IDomainEvent domainEvent, Guid aggregateId)
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.OutboxMessages.Add(new OutboxMessage(
            Guid.CreateVersion7(), DomainEventTypeRegistry.NameOf(domainEvent.GetType()), DomainEventTypeRegistry.Serialize(domainEvent), aggregateId, _clock.GetUtcNow()));
        await db.SaveChangesAsync(Ct);
    }

    private async Task DeliverEmailsAsync()
    {
        while (await _host.Processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    // ================================================================ tests

    [Fact]
    public void The_notice_handlers_run_before_every_other_OrderPaid_handler()
    {
        // The outbox stops a message at its first failing handler, so the buyer's notice must never sit behind Redis work.
        var handlers = _host.Services.GetServices<IntegrationEventHandlerRegistration>().Where(r => r.EventType == typeof(OrderPaid)).Select(r => r.HandlerType).ToList();

        handlers.Count.ShouldBeGreaterThan(1);
        handlers[0].ShouldBe(typeof(OrderPaidNotificationHandler));
    }

    [Fact]
    public async Task Replaying_the_same_OrderPaid_message_creates_one_notification_and_one_email_delivery()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await PaidOrderAsync(userId);

        await _host.DrainOutboxAsync(Ct);
        await _host.DrainOutboxAsync(Ct);

        // A second outbox row for the same order (a re-emitted event) with a different message id.
        await AddOutboxAsync(new OrderPaid(orderId, userId, _eventId, Guid.CreateVersion7(), 1_250_000, "VND", 1, _clock.GetUtcNow(), []), orderId);
        await _host.DrainOutboxAsync(Ct);

        // The first message handled again after its receipts were lost (the worst replay the outbox allows).
        await using (var scope = _host.Scope())
        {
            var db = NotificationTestHost.Db(scope);
            await db.ConsumerReceipts.ExecuteDeleteAsync(Ct);
            await db.OutboxMessages.ExecuteUpdateAsync(set => set.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null).SetProperty(m => m.NextAttemptAt, _clock.GetUtcNow()), Ct);
        }

        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.DedupeKey.ShouldBe($"order-paid:{orderId}");
        (await DeliveriesAsync()).ShouldHaveSingleItem().DedupeKey.ShouldBe($"order-paid:{orderId}");
        _sender.Sent.Count.ShouldBe(1);
        _notifier.Items.Count.ShouldBe(1, "only the write that created the notice pushes it");
        _notifier.Statuses.ShouldHaveSingleItem().ShouldBe((userId, orderId, "Paid"));
    }

    [Fact]
    public async Task OrderPaid_email_links_to_the_order_and_has_no_qr()
    {
        var (userId, address) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await PaidOrderAsync(userId);

        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.Type.ShouldBe(NotificationType.OrderPaid);
        notification.Link.ShouldBe($"/account/orders/{orderId}");
        notification.Title.ShouldBe("Thanh toán thành công");
        notification.Body.ShouldContain(_eventTitle);
        notification.Body.ShouldContain("1.250.000 ₫");

        var mail = _sender.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe(address);
        mail.HtmlBody.ShouldContain($"http://localhost:3000/account/orders/{orderId}");
        mail.HtmlBody.ShouldContain("Xem vé");
        mail.TextBody.ShouldContain(_eventTitle);
        mail.HtmlBody.ShouldContain("1.250.000 ₫");
        mail.HtmlBody.ShouldContain($"#{orderId.ToString()[..8].ToUpperInvariant()}");
        mail.HtmlBody.ShouldNotContain("qr", Case.Insensitive);
        mail.TextBody.ShouldNotContain("qr", Case.Insensitive);

        var push = _notifier.Items.ShouldHaveSingleItem();
        push.UserId.ShouldBe(userId);
        push.Item.Id.ShouldBe(notification.Id);
        push.Item.Link.ShouldBe(notification.Link);
        push.Item.Read.ShouldBeFalse();
    }

    [Fact]
    public async Task RefundRequested_and_OrderRefunded_each_notify_once_with_amount_in_vnd()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await OrderAsync(userId, o => o.MarkUnfulfillable());
        await _host.DrainOutboxAsync(Ct);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await using (var scope = _host.Scope())
        {
            var db = NotificationTestHost.Db(scope);
            (await db.Orders.SingleAsync(o => o.Id == orderId, Ct)).MarkRefunded();
            await db.SaveChangesAsync(Ct);
        }

        await _host.DrainOutboxAsync(Ct);
        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        var notices = await NotificationsAsync();
        notices.Select(n => (n.Type, n.DedupeKey)).ShouldBe(
        [
            (NotificationType.RefundRequested, $"refund-requested:{orderId}"),
            (NotificationType.Refunded, $"order-refunded:{orderId}"),
        ]);
        notices.ShouldAllBe(n => n.Link == $"/account/orders/{orderId}" && n.Body.Contains("1.250.000 ₫"));
        (await DeliveriesAsync()).Select(d => d.Template).Order().ShouldBe(["refund-requested", "refunded"]);
        _sender.Sent.Count.ShouldBe(2);
        _sender.Sent.ShouldAllBe(m => m.HtmlBody.Contains("1.250.000 ₫") && m.TextBody.Contains(_eventTitle));
        _notifier.Statuses.Select(s => s.Status).ShouldBe(["RefundPending", "Refunded"], "each push names the order's status at that moment");
    }

    [Fact]
    public async Task PaymentRefunded_notifies_the_order_owner()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await PaidOrderAsync(userId);
        await _host.DrainOutboxAsync(Ct);
        var attemptId = Guid.CreateVersion7();

        await AddOutboxAsync(new PaymentRefunded(attemptId, orderId, 300_000, "VND"), attemptId);
        await AddOutboxAsync(new PaymentRefunded(attemptId, orderId, 300_000, "VND"), attemptId);
        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        var refunded = (await NotificationsAsync()).Where(n => n.Type == NotificationType.Refunded).ShouldHaveSingleItem();
        refunded.UserId.ShouldBe(userId);
        refunded.DedupeKey.ShouldBe($"payment-refunded:{attemptId}");
        refunded.Body.ShouldContain("300.000 ₫");
        refunded.Link.ShouldBe($"/account/orders/{orderId}");
        var mail = _sender.Sent.Single(m => m.HtmlBody.Contains("300.000 ₫"));
        mail.TextBody.ShouldContain(_eventTitle);
        _notifier.Statuses.Last().ShouldBe((userId, orderId, "Paid"), "the order itself is unchanged by a session refund");
    }

    [Fact]
    public async Task A_failing_live_push_never_fails_the_handler_or_holds_back_the_later_order_paid_handlers()
    {
        _notifier.Throws = true;
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await PaidOrderAsync(userId);

        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();
        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        (await NotificationsAsync()).ShouldHaveSingleItem().DedupeKey.ShouldBe($"order-paid:{orderId}");
        (await DeliveriesAsync()).ShouldHaveSingleItem();
        _sender.Sent.Count.ShouldBe(1, "a second pass sends nothing extra");
        _notifier.Calls.ShouldBe(2, "each push is tried on its own, and the retry-free message is not pushed again");
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == orderId, Ct);
        message.ProcessedAt.ShouldNotBeNull();
        message.LastError.ShouldBeNull();
        message.Attempts.ShouldBe(1);
        (await db.ConsumerReceipts.AsNoTracking().Where(r => r.MessageId == message.Id).Select(r => r.Handler).ToListAsync(Ct)).ShouldBe(
        [
            typeof(OrderPaidNotificationHandler).FullName!,
            typeof(OrderPaidInventoryCommitHandler).FullName!,
            typeof(OrderPaidLeaveQueueHandler).FullName!,
            typeof(TicketRush.Application.Features.Waitlist.OrderPaidWaitlistHandler).FullName!,
            typeof(TicketRush.Infrastructure.Dashboard.DashboardOrderPaidHandler).FullName!,
            typeof(TicketRush.Application.Features.Observability.OrderPaidMetricHandler).FullName!,
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task OrderExpired_is_in_app_only()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        var orderId = await OrderAsync(userId, o => o.Expire());

        await _host.DrainOutboxAsync(Ct);
        await DeliverEmailsAsync();

        var notification = (await NotificationsAsync()).ShouldHaveSingleItem();
        notification.Type.ShouldBe(NotificationType.OrderExpired);
        notification.DedupeKey.ShouldBe($"order-expired:{orderId}");
        notification.Link.ShouldBe($"/account/orders/{orderId}");
        (await DeliveriesAsync()).ShouldBeEmpty();
        _sender.Sent.ShouldBeEmpty();
        _notifier.Statuses.ShouldHaveSingleItem().ShouldBe((userId, orderId, "Expired"));
    }

    [Fact]
    public async Task Handler_for_deleted_user_is_a_no_op()
    {
        var orderId = await PaidOrderAsync(Guid.CreateVersion7());

        await _host.DrainOutboxAsync(Ct);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await DeliveriesAsync()).ShouldBeEmpty();
        _notifier.Items.ShouldBeEmpty();
        _notifier.Statuses.ShouldBeEmpty();
        await using var scope = _host.Scope();
        var message = await NotificationTestHost.Db(scope).OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == orderId, Ct);
        message.ProcessedAt.ShouldNotBeNull();
        message.LastError.ShouldBeNull();
    }
}
