using System.Collections.Concurrent;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Remembers the text of every command the context sends to Postgres.</summary>
public sealed class CommandRecorder : DbCommandInterceptor
{
    private readonly ConcurrentQueue<string> _commands = new();

    public IReadOnlyCollection<string> Commands => _commands;

    public void Clear() => _commands.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        _commands.Enqueue(command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }
}

/// <summary>
/// The event-cancelled fan-out (spec 09): every holder (Paid, RefundPending, Refunded) and every follower is told once, whatever
/// the number of recipients, and redelivery or a second run tells nobody twice. Real Postgres, outbox, writer and mail processor.
/// </summary>
public sealed class EventCancelledNotificationTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string EventSlug = "son-tung-mtp-live";

    private readonly FakeTimeProvider _clock = new(NotificationTestHost.Start);
    private readonly ScriptedEmailSender _sender = new();
    private readonly RecordingUserNotifier _notifier = new();
    private readonly CommandRecorder _commands = new();
    private NotificationTestHost _host = null!;
    private Guid _eventId;
    private string _eventTitle = string.Empty;
    private DateTimeOffset _startsAt;
    private Guid _tierId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new NotificationTestHost(
            await containers.CreateDatabaseAsync(), containers.RedisConnectionString, _sender, _clock, notifier: _notifier, interceptor: _commands);
        await using var scope = _host.Scope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = NotificationTestHost.Db(scope);
        var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Slug == EventSlug, Ct);
        _eventId = evt.Id;
        _eventTitle = evt.Title;
        _startsAt = evt.StartsAt;
        _tierId = evt.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission).Id;
        await scope.ServiceProvider.GetRequiredService<InventorySeeder>().SeedAsync(_eventId, Ct);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    // ---------------------------------------------------------------- fixtures

    private async Task<Guid> OrderAsync(Guid userId, Action<Order> advance, Guid? eventId = null, bool startPayment = true)
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var now = _clock.GetUtcNow();
        var items = new[] { new OrderItem(_tierId, null, new Money(1_250_000), 1) };
        var order = Order.Create(userId, eventId ?? _eventId, Guid.CreateVersion7(), items, new Money(1_250_000), new BuyerInfo("Nguyễn Văn Đạt", "dat@example.com", null), now.AddMinutes(8), now);
        if (startPayment)
        {
            order.StartPayment(now);
        }

        advance(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync(Ct);
        return order.Id;
    }

    private Task<Guid> PaidAsync(Guid userId) => OrderAsync(userId, o => o.MarkPaid(_clock.GetUtcNow()));

    private Task<Guid> RefundPendingAsync(Guid userId) => OrderAsync(userId, o => o.MarkUnfulfillable());

    private Task<Guid> RefundedAsync(Guid userId) => OrderAsync(userId, o =>
    {
        o.MarkUnfulfillable();
        o.MarkRefunded();
    });

    private async Task FollowAsync(Guid userId, Guid? eventId = null)
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.EventFollows.Add(EventFollow.Create(userId, eventId ?? _eventId, _clock.GetUtcNow()));
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Guid> OtherEventIdAsync()
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).Events.AsNoTracking().Where(e => e.Id != _eventId).Select(e => e.Id).FirstAsync(Ct);
    }

    private async Task<Guid> UserAsync(bool emailConfirmed) => (await _host.CreateUserAsync(emailConfirmed)).Id;

    /// <summary>One delivery of the <see cref="EventCancelled"/> message to the notification handler.</summary>
    private async Task HandleAsync(CancellationToken ct)
    {
        await using var scope = _host.Scope();
        var handler = scope.ServiceProvider.GetRequiredService<EventCancelledNotificationHandler>();
        await handler.HandleAsync(new EventCancelled(_eventId, Guid.CreateVersion7()), new IntegrationEventContext(Guid.NewGuid(), _clock.GetUtcNow(), 1), ct);
    }

    private async Task<List<Notification>> NotificationsAsync(NotificationType type = NotificationType.EventCancelled)
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).Notifications.AsNoTracking().Where(n => n.Type == type).OrderBy(n => n.DedupeKey).ToListAsync(Ct);
    }

    private async Task<List<EmailDelivery>> DeliveriesAsync()
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).EmailDeliveries.AsNoTracking().Where(d => d.Template == "event-cancelled").OrderBy(d => d.DedupeKey).ToListAsync(Ct);
    }

    private string Key(Guid userId) => $"event-cancelled:{_eventId}:{userId}";

    // ================================================================ tests

    [Fact]
    public void The_notification_handler_runs_after_the_inventory_handler()
    {
        // Releasing holds and closing rooms is the safety-critical part of a cancellation: it goes first.
        var handlers = _host.Services.GetServices<IntegrationEventHandlerRegistration>().Where(r => r.EventType == typeof(EventCancelled)).Select(r => r.HandlerType).ToList();

        handlers.IndexOf(typeof(EventCancelledNotificationHandler)).ShouldBeGreaterThan(handlers.IndexOf(typeof(EventCancelledInventoryHandler)));
    }

    [Fact]
    public async Task Holders_and_followers_are_notified_once_each()
    {
        var paid = await UserAsync(emailConfirmed: true);
        var refundPending = await UserAsync(emailConfirmed: true);
        var refunded = await UserAsync(emailConfirmed: false);
        var holderAndFollower = await UserAsync(emailConfirmed: true);
        var followerOnly = await UserAsync(emailConfirmed: false);
        var followerWithMail = await UserAsync(emailConfirmed: true);
        await PaidAsync(paid);
        await RefundPendingAsync(refundPending);
        await RefundedAsync(refunded);
        await PaidAsync(holderAndFollower);
        await PaidAsync(holderAndFollower);
        await FollowAsync(holderAndFollower);
        await FollowAsync(followerOnly);
        await FollowAsync(followerWithMail);

        await HandleAsync(Ct);

        var notices = await NotificationsAsync();
        var everyone = new[] { paid, refundPending, refunded, holderAndFollower, followerOnly, followerWithMail };
        notices.Select(n => n.UserId).Order().ShouldBe(everyone.Order(), "one notice per user, even for a holder with two orders who also follows");
        notices.Select(n => n.DedupeKey).Order().ShouldBe(everyone.Select(Key).Order());
        notices.ShouldAllBe(n => n.Link == $"/events/{EventSlug}" && n.Title == "Sự kiện đã bị hủy" && n.ReadAt == null && n.CreatedAt == _clock.GetUtcNow());
        notices.ShouldAllBe(n => n.Body.Contains(_eventTitle) && n.Body.Contains(VietnamTimeText.Format(_startsAt)));

        var deliveries = await DeliveriesAsync();
        deliveries.Select(d => d.UserId).Order().ShouldBe(new[] { paid, refundPending, holderAndFollower, followerWithMail }.Order(), "email goes only to confirmed addresses");
        deliveries.ShouldAllBe(d => d.Status == EmailDeliveryStatus.Pending && d.Attempts == 0 && d.NextAttemptAt == _clock.GetUtcNow());
        deliveries.Select(d => d.DedupeKey).Order().ShouldBe(new[] { paid, refundPending, holderAndFollower, followerWithMail }.Select(Key).Order());
    }

    [Fact]
    public async Task The_email_model_matches_the_event_cancelled_template_and_renders()
    {
        var (userId, address) = await _host.CreateUserAsync(emailConfirmed: true);
        await FollowAsync(userId);

        await HandleAsync(Ct);
        while (await _host.Processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        var delivery = (await DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.ToAddress.ShouldBe(address);
        using var model = JsonDocument.Parse(delivery.ModelJson);
        model.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["eventTitle", "link", "name", "startsAtText"]);
        model.RootElement.GetProperty("link").GetString().ShouldBe($"http://localhost:3000/events/{EventSlug}");
        model.RootElement.GetProperty("name").GetString().ShouldBe("Nguyễn Văn Đạt");
        model.RootElement.GetProperty("eventTitle").GetString().ShouldBe(_eventTitle);
        model.RootElement.GetProperty("startsAtText").GetString().ShouldBe(VietnamTimeText.Format(_startsAt));
        model.RootElement.GetProperty("startsAtText").GetString().ShouldStartWith("19:30, ", Case.Sensitive, "the seeded event starts at 19:30 Vietnam time (12:30 UTC)");

        var mail = _sender.Sent.ShouldHaveSingleItem();
        mail.To.ShouldBe(address);
        mail.Subject.ShouldContain(_eventTitle);
        mail.HtmlBody.ShouldContain($"http://localhost:3000/events/{EventSlug}");
        mail.TextBody.ShouldContain(VietnamTimeText.Format(_startsAt));
    }

    [Fact]
    public async Task Redelivery_creates_no_duplicates()
    {
        var holder = await UserAsync(emailConfirmed: true);
        var follower = await UserAsync(emailConfirmed: true);
        await PaidAsync(holder);
        await FollowAsync(follower);

        await HandleAsync(Ct);
        await HandleAsync(Ct);

        (await NotificationsAsync()).Count.ShouldBe(2);
        (await DeliveriesAsync()).Count.ShouldBe(2);
        _notifier.Items.Count.ShouldBe(2, "the second run wrote nothing new, so it pushes nothing");
    }

    [Fact]
    public async Task Redelivery_through_the_outbox_creates_no_duplicates()
    {
        var holder = await UserAsync(emailConfirmed: true);
        var follower = await UserAsync(emailConfirmed: true);
        await PaidAsync(holder);
        await FollowAsync(follower);
        await _host.DrainOutboxAsync(Ct);
        await CancelEventAsync();

        await _host.DrainOutboxAsync(Ct);
        await ReplayEventCancelledMessageAsync();
        await _host.DrainOutboxAsync(Ct);

        (await NotificationsAsync()).Select(n => n.UserId).Order().ShouldBe(new[] { holder, follower }.Order());
        (await DeliveriesAsync()).Count.ShouldBe(2);
        _notifier.Items.Count(i => i.Item.Type == nameof(NotificationType.EventCancelled)).ShouldBe(2);
    }

    [Fact]
    public async Task Unrelated_users_and_pending_orders_are_not_notified()
    {
        var other = await OtherEventIdAsync();
        var pending = await UserAsync(emailConfirmed: true);
        var awaitingPayment = await UserAsync(emailConfirmed: true);
        var expired = await UserAsync(emailConfirmed: true);
        var otherEventHolder = await UserAsync(emailConfirmed: true);
        var otherEventFollower = await UserAsync(emailConfirmed: true);
        await UserAsync(emailConfirmed: true);
        await OrderAsync(pending, _ => { }, startPayment: false);
        await OrderAsync(awaitingPayment, _ => { });
        await OrderAsync(expired, o => o.Expire());
        await OrderAsync(otherEventHolder, o => o.MarkPaid(_clock.GetUtcNow()), other);
        await FollowAsync(otherEventFollower, other);

        await HandleAsync(Ct);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await DeliveriesAsync()).ShouldBeEmpty();
        _notifier.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_holder_or_follower_whose_user_no_longer_exists_is_skipped()
    {
        var follower = await UserAsync(emailConfirmed: true);
        await FollowAsync(follower);
        await FollowAsync(Guid.CreateVersion7());
        await PaidAsync(Guid.CreateVersion7());

        await HandleAsync(Ct);

        (await NotificationsAsync()).ShouldHaveSingleItem().UserId.ShouldBe(follower);
    }

    [Fact]
    public async Task A_missing_event_is_a_no_op()
    {
        await using var scope = _host.Scope();
        var handler = scope.ServiceProvider.GetRequiredService<EventCancelledNotificationHandler>();

        await handler.HandleAsync(new EventCancelled(Guid.CreateVersion7(), Guid.CreateVersion7()), new IntegrationEventContext(Guid.NewGuid(), _clock.GetUtcNow(), 1), Ct);

        (await NotificationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_users_notified_by_this_run_are_pushed()
    {
        var first = await UserAsync(emailConfirmed: true);
        var late = await UserAsync(emailConfirmed: true);
        await FollowAsync(first);
        await HandleAsync(Ct);
        await FollowAsync(late);

        await HandleAsync(Ct);

        _notifier.Items.Select(i => i.UserId).ShouldBe([first, late], ignoreOrder: true);
        var notices = await NotificationsAsync();
        foreach (var (userId, item) in _notifier.Items)
        {
            var stored = notices.Single(n => n.UserId == userId);
            item.ShouldBe(new NotificationItem(stored.Id, nameof(NotificationType.EventCancelled), stored.Title, stored.Body, stored.Link, false, stored.CreatedAt));
        }
    }

    [Fact]
    public async Task A_failing_live_push_does_not_fail_the_handler_or_undo_the_notices()
    {
        _notifier.Throws = true;
        var first = await UserAsync(emailConfirmed: true);
        var second = await UserAsync(emailConfirmed: true);
        await FollowAsync(first);
        await FollowAsync(second);

        await Should.NotThrowAsync(() => HandleAsync(Ct));

        (await NotificationsAsync()).Count.ShouldBe(2);
        (await DeliveriesAsync()).Count.ShouldBe(2);
        _notifier.Calls.ShouldBe(2, "each push is tried on its own");
    }

    [Fact]
    public async Task A_cancelled_push_stops_the_handler()
    {
        // Genuine cancellation (the worker is stopping) is not a "failed push": it must reach the outbox, which then retries the message.
        using var cts = new CancellationTokenSource();
        _notifier.CancelOnPush = cts;
        await FollowAsync(await UserAsync(emailConfirmed: true));

        await Should.ThrowAsync<OperationCanceledException>(() => HandleAsync(cts.Token));
    }

    [Fact]
    public async Task Ten_thousand_recipients_use_a_constant_number_of_statements()
    {
        const int Followers = 10_000;
        await SeedFollowersAsync(Followers);
        var holder = await UserAsync(emailConfirmed: true);
        await PaidAsync(holder);
        _commands.Clear();

        await HandleAsync(Ct);

        _commands.Commands.Count.ShouldBeLessThanOrEqualTo(4, string.Join("\n---\n", _commands.Commands));
        (await NotificationsAsync()).Count.ShouldBe(Followers + 1);
        (await DeliveriesAsync()).Count.ShouldBe((Followers / 2) + 1, "every second bulk user has a confirmed address, and so does the holder");
        _notifier.Items.Count.ShouldBe(Followers + 1);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Bulk users and follows in two statements (not 10 000 entities); every second user has a confirmed address.</summary>
    private async Task SeedFollowersAsync(int count)
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var now = _clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO users (id, user_name, normalized_user_name, email, normalized_email, email_confirmed, full_name,
                               access_failed_count, lockout_enabled, phone_number_confirmed, two_factor_enabled, created_at, updated_at)
            SELECT gen_random_uuid(), 'bulk-' || g || '@example.com', 'BULK-' || g || '@EXAMPLE.COM', 'bulk-' || g || '@example.com',
                   'BULK-' || g || '@EXAMPLE.COM', g % 2 = 0, 'Người dùng ' || g, 0, false, false, false, {now}, {now}
            FROM generate_series(1, {count}) AS g
            """,
            Ct);
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO event_follows (user_id, event_id, created_at)
            SELECT id, {_eventId}, {now} FROM users WHERE user_name LIKE 'bulk-%'
            """,
            Ct);
    }

    private async Task CancelEventAsync()
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        (await db.Events.SingleAsync(e => e.Id == _eventId, Ct)).Cancel();
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>The first <see cref="EventCancelled"/> message handled again after its receipts were lost (the worst replay the outbox allows).</summary>
    private async Task ReplayEventCancelledMessageAsync()
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var type = DomainEventTypeRegistry.NameOf(typeof(EventCancelled));
        await db.ConsumerReceipts.ExecuteDeleteAsync(Ct);
        await db.OutboxMessages.Where(m => m.Type == type)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null).SetProperty(m => m.NextAttemptAt, _clock.GetUtcNow()), Ct);
    }
}
