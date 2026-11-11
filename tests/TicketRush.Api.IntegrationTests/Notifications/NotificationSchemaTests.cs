using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Spec 09 persistence: the unique keys that make notices, follows and reminders idempotent, and the indexes the reads rely on.</summary>
public sealed class NotificationSchemaTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string UniqueViolation = "23505";

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(await containers.CreateDatabaseAsync(), containers.RedisConnectionString));
        builder.AddPersistence();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        _host = builder.Build();
    }

    public ValueTask DisposeAsync()
    {
        _host.Dispose();
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AsyncServiceScope Scope() => _host.Services.CreateAsyncScope();

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private async Task SaveAsync(params object[] entities)
    {
        await using var scope = Scope();
        var db = Db(scope);
        db.AddRange(entities);
        await db.SaveChangesAsync(Ct);
    }

    private static async Task ShouldViolateUniqueAsync(Func<Task> act)
    {
        var exception = await Should.ThrowAsync<DbUpdateException>(act);
        exception.InnerException.ShouldBeOfType<PostgresException>().SqlState.ShouldBe(UniqueViolation);
    }

    private static Notification NewNotification(Guid userId, string dedupeKey) =>
        Notification.Create(userId, NotificationType.OrderPaid, "Order paid", "Your tickets are ready.", "/account/orders/1", dedupeKey, Now);

    [Fact]
    public async Task The_migrated_schema_matches_the_model()
    {
        await using var scope = Scope();
        Db(scope).Database.HasPendingModelChanges().ShouldBeFalse();
        (await Db(scope).Database.GetPendingMigrationsAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_notification_dedupe_key_is_a_unique_violation()
    {
        await SaveAsync(NewNotification(Guid.CreateVersion7(), "order-paid:1"));

        await ShouldViolateUniqueAsync(() => SaveAsync(NewNotification(Guid.CreateVersion7(), "order-paid:1")));
    }

    [Fact]
    public async Task Duplicate_email_delivery_dedupe_key_is_a_unique_violation()
    {
        await SaveAsync(EmailDelivery.Create(Guid.CreateVersion7(), "a@example.com", "order-paid", "{}", "order-paid:1", Now));

        await ShouldViolateUniqueAsync(() => SaveAsync(EmailDelivery.Create(Guid.CreateVersion7(), "b@example.com", "order-paid", "{}", "order-paid:1", Now)));
    }

    [Fact]
    public async Task Duplicate_follow_is_a_unique_violation()
    {
        var userId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        await SaveAsync(EventFollow.Create(userId, eventId, Now));

        await ShouldViolateUniqueAsync(() => SaveAsync(EventFollow.Create(userId, eventId, Now)));
        await SaveAsync(EventFollow.Create(userId, Guid.CreateVersion7(), Now), EventFollow.Create(Guid.CreateVersion7(), eventId, Now));
    }

    [Fact]
    public async Task Duplicate_reminder_dispatch_is_a_unique_violation()
    {
        var userId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        await SaveAsync(ReminderDispatch.Create(userId, eventId, ReminderKind.SaleOpens30m, Now));

        await ShouldViolateUniqueAsync(() => SaveAsync(ReminderDispatch.Create(userId, eventId, ReminderKind.SaleOpens30m, Now)));
        await SaveAsync(ReminderDispatch.Create(userId, eventId, ReminderKind.EventStarts24h, Now));
    }

    [Fact]
    public async Task A_saved_reminder_dispatch_writes_ReminderDue_to_the_outbox()
    {
        var userId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();

        await SaveAsync(ReminderDispatch.Create(userId, eventId, ReminderKind.EventStarts24h, Now));

        await using var scope = Scope();
        var message = await Db(scope).OutboxMessages.SingleAsync(m => m.Type == nameof(ReminderDue), Ct);
        var due = (ReminderDue)new DomainEventTypeRegistry([]).Deserialize(message);
        due.ShouldBe(new ReminderDue(userId, eventId, ReminderKind.EventStarts24h));
    }

    [Fact]
    public async Task A_notification_and_a_delivery_round_trip_with_enums_as_text_and_the_model_as_jsonb()
    {
        var userId = Guid.CreateVersion7();
        var notification = NewNotification(userId, "order-paid:2");
        var delivery = EmailDelivery.Create(userId, "a@example.com", "order-paid", "{\"orderId\":\"2\"}", "order-paid:2", Now);
        delivery.RecordFailure(Now, new string('x', 3000), TimeSpan.FromMinutes(1), deadLetter: false);
        notification.MarkRead(Now.AddMinutes(1));
        await SaveAsync(notification, delivery);

        await using var scope = Scope();
        var db = Db(scope);
        var loadedNotification = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == notification.Id, Ct);
        loadedNotification.Type.ShouldBe(NotificationType.OrderPaid);
        loadedNotification.ReadAt.ShouldBe(Now.AddMinutes(1));
        loadedNotification.Link.ShouldBe("/account/orders/1");

        var loadedDelivery = await db.EmailDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id, Ct);
        loadedDelivery.Status.ShouldBe(EmailDeliveryStatus.Pending);
        loadedDelivery.Attempts.ShouldBe(1);
        loadedDelivery.NextAttemptAt.ShouldBe(Now.AddMinutes(1));
        loadedDelivery.LastError!.Length.ShouldBe(EmailDelivery.MaxErrorLength);

        (await db.Database.SqlQuery<string>($"SELECT type AS \"Value\" FROM notifications WHERE id = {notification.Id}").SingleAsync(Ct)).ShouldBe("OrderPaid");
        (await db.Database.SqlQuery<string>($"SELECT pg_typeof(model)::text AS \"Value\" FROM email_deliveries WHERE id = {delivery.Id}").SingleAsync(Ct)).ShouldBe("jsonb");
    }

    [Fact]
    public async Task Keyset_index_exists()
    {
        await using var scope = Scope();
        var definitions = await Db(scope).Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('notifications', 'email_deliveries')")
            .ToListAsync(Ct);

        definitions.ShouldContain(d => d.Contains("(user_id, created_at DESC, id DESC)"));
        definitions.ShouldContain(d => d.Contains("(user_id)") && d.Contains("WHERE (read_at IS NULL)"));
        definitions.ShouldContain(d => d.Contains("(next_attempt_at)") && d.Contains("WHERE ((status)::text = 'Pending'::text)"));
    }

    [Fact]
    public async Task Event_follows_are_indexed_by_event_id()
    {
        // Serves the reminder planner's join and the cancelled-event fan-out, which both look followers up by event.
        await using var scope = Scope();
        var definitions = await Db(scope).Database
            .SqlQuery<string>($"SELECT indexdef AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'event_follows'")
            .ToListAsync(Ct);

        definitions.ShouldContain(d => d.Contains("btree (event_id)"));
    }
}
