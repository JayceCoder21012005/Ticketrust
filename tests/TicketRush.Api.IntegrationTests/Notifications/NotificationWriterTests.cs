using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;

namespace TicketRush.Api.IntegrationTests.Notifications;

public sealed class NotificationWriterTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(NotificationTestHost.Start);
    private NotificationTestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = new NotificationTestHost(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, new ScriptedEmailSender(), _clock);

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NotificationDraft Draft(Guid userId, string key, EmailPlan? email = null) =>
        new(userId, NotificationType.OrderPaid, "Thanh toán thành công", "Vé của bạn đã sẵn sàng.", "/account/orders/1", key, email);

    private static EmailPlan Plan() => new("order-paid", NotificationTestHost.OrderModel());

    private async Task<(int Notifications, int Deliveries)> CountAsync()
    {
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        return (await db.Notifications.CountAsync(Ct), await db.EmailDeliveries.CountAsync(Ct));
    }

    [Fact]
    public async Task Same_dedupe_key_twice_writes_one_notification_and_one_delivery()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);

        var first = await _host.AddAsync(Draft(userId, "order-paid:1", Plan()), Ct);
        var second = await _host.AddAsync(Draft(userId, "order-paid:1", Plan()), Ct);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
        (await CountAsync()).ShouldBe((1, 1));
    }

    [Fact]
    public async Task A_confirmed_user_gets_the_notice_and_a_pending_delivery_with_the_stored_model()
    {
        var (userId, email) = await _host.CreateUserAsync(emailConfirmed: true);

        var id = await _host.AddAsync(Draft(userId, "order-paid:2", Plan()), Ct);

        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var notification = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        notification.Id.ShouldBe(id!.Value);
        notification.UserId.ShouldBe(userId);
        notification.Type.ShouldBe(NotificationType.OrderPaid);
        notification.Title.ShouldBe("Thanh toán thành công");
        notification.Link.ShouldBe("/account/orders/1");
        notification.CreatedAt.ShouldBe(NotificationTestHost.Start);
        notification.ReadAt.ShouldBeNull();

        var delivery = await db.EmailDeliveries.AsNoTracking().SingleAsync(Ct);
        delivery.UserId.ShouldBe(userId);
        delivery.ToAddress.ShouldBe(email);
        delivery.Template.ShouldBe("order-paid");
        delivery.DedupeKey.ShouldBe("order-paid:2");
        delivery.Status.ShouldBe(EmailDeliveryStatus.Pending);
        delivery.Attempts.ShouldBe(0);
        delivery.NextAttemptAt.ShouldBe(NotificationTestHost.Start);
        delivery.ModelJson.ShouldContain("\"orderCode\"");
    }

    [Fact]
    public async Task Unconfirmed_email_gets_in_app_only()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: false);

        var id = await _host.AddAsync(Draft(userId, "order-paid:3", Plan()), Ct);

        id.ShouldNotBeNull();
        (await CountAsync()).ShouldBe((1, 0));
    }

    [Fact]
    public async Task A_draft_without_an_email_plan_gets_in_app_only_and_is_still_deduplicated()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);

        (await _host.AddAsync(Draft(userId, "order-expired:1"), Ct)).ShouldNotBeNull();
        (await _host.AddAsync(Draft(userId, "order-expired:1"), Ct)).ShouldBeNull();

        (await CountAsync()).ShouldBe((1, 0));
    }

    [Fact]
    public async Task Missing_user_writes_nothing()
    {
        var id = await _host.AddAsync(Draft(Guid.CreateVersion7(), "order-paid:4", Plan()), Ct);

        id.ShouldBeNull();
        (await CountAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_duplicate_inside_a_transaction_does_not_abort_it()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        var writer = scope.ServiceProvider.GetRequiredService<INotificationWriter>();

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            (await writer.AddAsync(Draft(userId, "order-paid:5", Plan()), Ct)).ShouldNotBeNull();
            (await writer.AddAsync(Draft(userId, "order-paid:5", Plan()), Ct)).ShouldBeNull();
            (await writer.AddAsync(Draft(userId, "order-paid:6", Plan()), Ct)).ShouldNotBeNull("the transaction is still usable after the duplicate");
            await transaction.CommitAsync(Ct);
        }

        (await CountAsync()).ShouldBe((2, 2));
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_row()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);

        await using (var transaction = await db.Database.BeginTransactionAsync(Ct))
        {
            await scope.ServiceProvider.GetRequiredService<INotificationWriter>().AddAsync(Draft(userId, "order-paid:7", Plan()), Ct);
            await transaction.RollbackAsync(Ct);
        }

        (await CountAsync()).ShouldBe((0, 0));
    }

    [Fact]
    public async Task A_model_that_is_not_a_flat_object_of_strings_is_rejected_before_anything_is_written()
    {
        var (userId, _) = await _host.CreateUserAsync(emailConfirmed: true);

        await Should.ThrowAsync<ArgumentException>(() => _host.AddAsync(Draft(userId, "order-paid:8", new EmailPlan("order-paid", new { amount = 5 })), Ct));

        (await CountAsync()).ShouldBe((0, 0));
    }
}
