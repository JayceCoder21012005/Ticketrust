using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Features.Notifications;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>
/// What a due reminder becomes (spec 09): an in-app notice and an email through the writer, idempotent on the business key
/// <c>reminder:{kind}:{eventId}:{userId}</c>, with a guarded live push. Real outbox, handler, writer and mail processor.
/// </summary>
public sealed class ReminderDueTests(ContainersFixture containers) : IAsyncLifetime
{
    private ReminderTestWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => NotificationTestHost.Start;

    public async ValueTask InitializeAsync() => _world = await ReminderTestWorld.CreateAsync(containers, Ct);

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    // ---------------------------------------------------------------- fixtures

    /// <param name="plannedAt">When the reminder was planned (the message's OccurredAt); defaults to the clock's current time.</param>
    private async Task HandleAsync(ReminderDue message, CancellationToken ct, DateTimeOffset? plannedAt = null)
    {
        await using var scope = _world.Host.Scope();
        var handler = scope.ServiceProvider.GetRequiredService<ReminderDueHandler>();
        await handler.HandleAsync(message, new IntegrationEventContext(Guid.NewGuid(), plannedAt ?? _world.Clock.GetUtcNow(), 1), ct);
    }

    private async Task<List<Notification>> NotificationsAsync()
    {
        await using var scope = _world.Host.Scope();
        return await NotificationTestHost.Db(scope).Notifications.AsNoTracking().OrderBy(n => n.DedupeKey).ToListAsync(Ct);
    }

    private async Task<List<EmailDelivery>> DeliveriesAsync()
    {
        await using var scope = _world.Host.Scope();
        return await NotificationTestHost.Db(scope).EmailDeliveries.AsNoTracking().OrderBy(d => d.DedupeKey).ToListAsync(Ct);
    }

    private static string Key(ReminderKind kind, Guid eventId, Guid userId) => $"reminder:{kind}:{eventId}:{userId}";

    // ================================================================ tests

    [Fact]
    public async Task ReminderDue_handler_writes_notification_and_email_once_on_redelivery()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        await _world.FollowAsync(userId, evt.Id, Ct);
        await _world.PlanAsync(Ct);

        await _world.Host.DrainOutboxAsync(Ct);
        await ReplayReminderMessagesAsync();
        await _world.Host.DrainOutboxAsync(Ct);

        var notices = await NotificationsAsync();
        notices.Select(n => n.DedupeKey).ShouldBe(
            [Key(ReminderKind.EventStarts24h, evt.Id, userId), Key(ReminderKind.SaleOpens30m, evt.Id, userId)],
            ignoreOrder: true);
        (await DeliveriesAsync()).Count.ShouldBe(2, "the replay wrote no second email");
        _world.Notifier.Items.Count.ShouldBe(2, "the replay wrote nothing new, so it pushes nothing");
    }

    [Fact]
    public async Task The_sale_reminder_names_the_earliest_opening_phase_and_the_start_reminder_the_start()
    {
        var (userId, address) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var startsAt = Now.AddHours(12);
        var evt = await _world.EventAsync(EventStatus.Published, startsAt, Ct, Now.AddMinutes(25), Now.AddMinutes(10));

        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.SaleOpens30m), Ct);
        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h), Ct);

        var notices = (await NotificationsAsync()).ToDictionary(n => n.Type);
        var sale = notices[NotificationType.SaleOpensSoon];
        sale.UserId.ShouldBe(userId);
        sale.Link.ShouldBe($"/events/{evt.Slug}");
        sale.Title.ShouldBe("Sắp mở bán vé");
        sale.Body.ShouldContain(evt.Title);
        sale.Body.ShouldContain(VietnamTimeText.Format(Now.AddMinutes(10)));
        sale.Body.ShouldNotContain(VietnamTimeText.Format(Now.AddMinutes(25)), Case.Sensitive, "the earliest phase is the one named");
        sale.ReadAt.ShouldBeNull();
        sale.CreatedAt.ShouldBe(Now);
        var start = notices[NotificationType.EventStartsSoon];
        start.Link.ShouldBe($"/events/{evt.Slug}");
        start.Title.ShouldBe("Sự kiện sắp diễn ra");
        start.Body.ShouldContain(evt.Title);
        start.Body.ShouldContain(VietnamTimeText.Format(startsAt));

        var deliveries = (await DeliveriesAsync()).ToDictionary(d => d.Template);
        deliveries.Keys.Order().ShouldBe(["reminder-event-starts", "reminder-sale-opens"]);
        deliveries["reminder-sale-opens"].DedupeKey.ShouldBe(sale.DedupeKey);
        AssertModel(deliveries["reminder-sale-opens"], evt, VietnamTimeText.Format(Now.AddMinutes(10)));
        AssertModel(deliveries["reminder-event-starts"], evt, VietnamTimeText.Format(startsAt));

        while (await _world.Host.Processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        _world.Sender.Sent.Count.ShouldBe(2, "both templates render with the model the handler built");
        _world.Sender.Sent.ShouldAllBe(m => m.To == address && m.HtmlBody.Contains($"http://localhost:3000/events/{evt.Slug}"));
    }

    [Fact]
    public async Task A_user_without_a_confirmed_email_gets_the_notice_only()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: false);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);

        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h), Ct);

        (await NotificationsAsync()).ShouldHaveSingleItem();
        (await DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_user_or_event_writes_nothing()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);

        await Should.NotThrowAsync(() => HandleAsync(new ReminderDue(Guid.CreateVersion7(), evt.Id, ReminderKind.EventStarts24h), Ct));
        await Should.NotThrowAsync(() => HandleAsync(new ReminderDue(userId, Guid.CreateVersion7(), ReminderKind.EventStarts24h), Ct));

        (await NotificationsAsync()).ShouldBeEmpty();
        (await DeliveriesAsync()).ShouldBeEmpty();
        _world.Notifier.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_event_cancelled_since_the_reminder_was_planned_is_not_announced()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Cancelled, Now.AddHours(12), Ct, Now.AddMinutes(10));

        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h), Ct);
        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.SaleOpens30m), Ct);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sale_reminder_whose_phase_has_already_opened_writes_nothing()
    {
        // The only phase opened before the reminder was planned: there is no opening time left to announce.
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);

        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.SaleOpens30m), Ct);

        (await NotificationsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_start_reminder_handled_after_the_event_started_writes_nothing()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);
        var message = new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h);

        _world.Clock.Advance(TimeSpan.FromHours(12));
        await HandleAsync(message, Ct, plannedAt: Now);
        _world.Clock.Advance(TimeSpan.FromHours(1));
        await HandleAsync(message, Ct, plannedAt: Now);

        (await NotificationsAsync()).ShouldBeEmpty("exactly at the start, and after it, the moment has passed");
        (await DeliveriesAsync()).ShouldBeEmpty();
        _world.Notifier.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sale_reminder_handled_after_the_sale_opened_writes_nothing()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(10));
        var message = new ReminderDue(userId, evt.Id, ReminderKind.SaleOpens30m);

        _world.Clock.Advance(TimeSpan.FromMinutes(10));
        await HandleAsync(message, Ct, plannedAt: Now);
        _world.Clock.Advance(TimeSpan.FromMinutes(3));
        await HandleAsync(message, Ct, plannedAt: Now);

        (await NotificationsAsync()).ShouldBeEmpty();
        (await DeliveriesAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reminder_handled_just_before_its_moment_is_still_written()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));

        _world.Clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        await HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.SaleOpens30m), Ct, plannedAt: Now);

        (await NotificationsAsync()).ShouldHaveSingleItem().Type.ShouldBe(NotificationType.SaleOpensSoon);
    }

    [Fact]
    public async Task A_fan_out_to_hundreds_of_followers_drains_in_a_bounded_number_of_batches()
    {
        // Reminders carry no aggregate id: the dispatcher claims a whole batch of them at once. With the event's id on each,
        // only one per batch was claimable and 300 followers needed 300 batches.
        const int Followers = 300;
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(10));
        await _world.SeedFollowingUsersAsync(evt.Id, Followers, Ct);
        (await _world.PlanAsync(Ct)).ShouldBe(Followers);

        var batches = 0;
        while (await _world.Host.Outbox.ProcessBatchAsync(Ct) > 0)
        {
            batches++;
        }

        var batchSize = _world.Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>().Value.BatchSize;
        batches.ShouldBeLessThanOrEqualTo(((Followers + batchSize - 1) / batchSize) + 2);
        var messages = await _world.ReminderMessagesAsync(Ct);
        messages.Count.ShouldBe(Followers);
        messages.ShouldAllBe(m => m.ProcessedAt != null && m.LastError == null);
        (await NotificationsAsync()).Count.ShouldBe(Followers);
        (await DeliveriesAsync()).Count.ShouldBe(Followers);
    }

    [Fact]
    public async Task Only_a_new_notice_is_pushed_live_and_the_push_matches_the_stored_row()
    {
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);
        var message = new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h);

        await HandleAsync(message, Ct);
        await HandleAsync(message, Ct);

        var stored = (await NotificationsAsync()).ShouldHaveSingleItem();
        var (pushedTo, item) = _world.Notifier.Items.ShouldHaveSingleItem();
        pushedTo.ShouldBe(userId);
        item.ShouldBe(new NotificationItem(stored.Id, nameof(NotificationType.EventStartsSoon), stored.Title, stored.Body, stored.Link, false, stored.CreatedAt));
    }

    [Fact]
    public async Task A_failing_live_push_does_not_fail_the_handler_or_undo_the_notice()
    {
        _world.Notifier.Throws = true;
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);

        await Should.NotThrowAsync(() => HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h), Ct));

        (await NotificationsAsync()).ShouldHaveSingleItem();
        (await DeliveriesAsync()).ShouldHaveSingleItem();
        _world.Notifier.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_cancelled_push_stops_the_handler()
    {
        // Genuine cancellation (the worker is stopping) is not a "failed push": it must reach the outbox, which retries the message.
        using var cts = new CancellationTokenSource();
        _world.Notifier.CancelOnPush = cts;
        var (userId, _) = await _world.Host.CreateUserAsync(emailConfirmed: true);
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => HandleAsync(new ReminderDue(userId, evt.Id, ReminderKind.EventStarts24h), cts.Token));
    }

    [Fact]
    public void The_reminder_handler_is_registered_for_the_outbox()
    {
        var registered = _world.Host.Services.GetServices<IntegrationEventHandlerRegistration>()
            .Where(r => r.EventType == typeof(ReminderDue)).Select(r => r.HandlerType);

        registered.ShouldBe([typeof(ReminderDueHandler)]);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertModel(EmailDelivery delivery, Event evt, string startsAtText)
    {
        using var model = JsonDocument.Parse(delivery.ModelJson);
        model.RootElement.EnumerateObject().Select(p => p.Name).Order().ShouldBe(["eventTitle", "link", "name", "startsAtText"]);
        model.RootElement.GetProperty("link").GetString().ShouldBe($"http://localhost:3000/events/{evt.Slug}");
        model.RootElement.GetProperty("eventTitle").GetString().ShouldBe(evt.Title);
        model.RootElement.GetProperty("startsAtText").GetString().ShouldBe(startsAtText);
        model.RootElement.GetProperty("name").GetString().ShouldBe("Nguyễn Văn Đạt");
    }

    /// <summary>The reminder messages delivered again after their receipts were lost (the worst replay the outbox allows).</summary>
    private async Task ReplayReminderMessagesAsync()
    {
        await using var scope = _world.Host.Scope();
        var db = NotificationTestHost.Db(scope);
        var type = DomainEventTypeRegistry.NameOf(typeof(ReminderDue));
        await db.ConsumerReceipts.ExecuteDeleteAsync(Ct);
        await db.OutboxMessages.Where(m => m.Type == type)
            .ExecuteUpdateAsync(set => set.SetProperty(m => m.ProcessedAt, (DateTimeOffset?)null).SetProperty(m => m.NextAttemptAt, _world.Clock.GetUtcNow()), Ct);
    }
}
