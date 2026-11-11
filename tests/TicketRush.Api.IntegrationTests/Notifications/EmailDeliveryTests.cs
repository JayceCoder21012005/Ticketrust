using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;

namespace TicketRush.Api.IntegrationTests.Notifications;

public sealed class EmailDeliveryTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(NotificationTestHost.Start);
    private readonly ScriptedEmailSender _sender = new();
    private readonly CapturingLoggerProvider _logs = new();
    private string _database = null!;
    private NotificationTestHost _host = null!;
    private int _sequence;

    public async ValueTask InitializeAsync()
    {
        _database = await containers.CreateDatabaseAsync();
        _host = NewHost();
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private NotificationTestHost NewHost(int batchSize = 20, TimeSpan? sendTimeout = null) =>
        new(_database, containers.RedisConnectionString, _sender, _clock, batchSize, _logs, sendTimeout);

    private async Task<EmailDelivery> SeedAsync(string? to = null, string template = "order-paid", string? modelJson = null)
    {
        var number = Interlocked.Increment(ref _sequence);
        var delivery = EmailDelivery.Create(
            Guid.CreateVersion7(),
            to ?? $"buyer{number}@example.com",
            template,
            modelJson ?? System.Text.Json.JsonSerializer.Serialize(NotificationTestHost.OrderModel($"TR-{number:D4}")),
            $"order-paid:{number}",
            _clock.GetUtcNow());
        await using var scope = _host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.EmailDeliveries.Add(delivery);
        await db.SaveChangesAsync(Ct);
        return delivery;
    }

    private async Task<EmailDelivery> LoadAsync(Guid id)
    {
        await using var scope = _host.Scope();
        return await NotificationTestHost.Db(scope).EmailDeliveries.AsNoTracking().SingleAsync(d => d.Id == id, Ct);
    }

    [Fact]
    public async Task Delivery_is_sent_once_and_marked_sent()
    {
        var delivery = await SeedAsync();

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(0, "a sent delivery is never claimed again");

        _sender.Sent.Count.ShouldBe(1);
        var loaded = await LoadAsync(delivery.Id);
        loaded.Status.ShouldBe(EmailDeliveryStatus.Sent);
        loaded.SentAt.ShouldBe(_clock.GetUtcNow());
        loaded.Attempts.ShouldBe(0);
    }

    [Fact]
    public async Task Sent_email_uses_delivery_id_as_message_id()
    {
        var delivery = await SeedAsync();

        await _host.Processor.ProcessBatchAsync(Ct);

        var message = _sender.Sent.ShouldHaveSingleItem();
        message.MessageId.ShouldBe(delivery.Id);
        message.To.ShouldBe(delivery.ToAddress);
        message.Subject.ShouldBe("Thanh toán thành công, đơn hàng TR-0001");
        message.HtmlBody.ShouldContain("Xin chào Nguyễn Văn Đạt");
        message.TextBody.ShouldContain("TR-0001");
    }

    [Fact]
    public async Task Smtp_failure_reschedules_with_backoff_and_does_not_touch_other_deliveries()
    {
        var failing = await SeedAsync(to: "refused@example.com");
        var other = await SeedAsync();
        _sender.RefusedAddresses.Add("refused@example.com");

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(2);

        _sender.Sent.ShouldHaveSingleItem().MessageId.ShouldBe(other.Id);
        var sent = await LoadAsync(other.Id);
        sent.Status.ShouldBe(EmailDeliveryStatus.Sent);
        sent.Attempts.ShouldBe(0);
        sent.LastError.ShouldBeNull();

        var retry = await LoadAsync(failing.Id);
        retry.Status.ShouldBe(EmailDeliveryStatus.Pending);
        retry.Attempts.ShouldBe(1);
        retry.SentAt.ShouldBeNull();
        retry.LastError.ShouldStartWith("InvalidOperationException");
        (retry.NextAttemptAt - _clock.GetUtcNow()).ShouldBeInRange(OutboxRetryPolicy.BaseDelay / 2, OutboxRetryPolicy.BaseDelay);

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(0, "the failed delivery is not due yet");

        _sender.RefusedAddresses.Clear();
        _clock.Advance(OutboxRetryPolicy.BaseDelay);
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await LoadAsync(failing.Id)).Status.ShouldBe(EmailDeliveryStatus.Sent);
    }

    [Fact]
    public async Task Backoff_grows_and_dead_letters_after_max_attempts()
    {
        var delivery = await SeedAsync(to: "refused@example.com");
        _sender.RefusedAddresses.Add("refused@example.com");

        var previousCeiling = TimeSpan.Zero;
        for (var attempt = 1; attempt < OutboxRetryPolicy.MaxAttempts; attempt++)
        {
            (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);

            var loaded = await LoadAsync(delivery.Id);
            loaded.Status.ShouldBe(EmailDeliveryStatus.Pending);
            loaded.Attempts.ShouldBe(attempt);
            var ceiling = TimeSpan.FromMilliseconds(Math.Min(OutboxRetryPolicy.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1), OutboxRetryPolicy.MaxDelay.TotalMilliseconds));
            (loaded.NextAttemptAt - _clock.GetUtcNow()).ShouldBeInRange(ceiling / 2, ceiling);
            ceiling.ShouldBeGreaterThanOrEqualTo(previousCeiling);
            previousCeiling = ceiling;

            _clock.Advance(loaded.NextAttemptAt - _clock.GetUtcNow());
        }

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);

        var parked = await LoadAsync(delivery.Id);
        parked.Status.ShouldBe(EmailDeliveryStatus.DeadLettered);
        parked.Attempts.ShouldBe(OutboxRetryPolicy.MaxAttempts);
        _clock.Advance(TimeSpan.FromDays(1));
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(0, "a dead-lettered delivery is never retried");
        _sender.Attempts.ShouldBe(OutboxRetryPolicy.MaxAttempts);
    }

    [Fact]
    public async Task Two_processors_never_send_the_same_row()
    {
        const int Rows = 100;
        for (var i = 0; i < Rows; i++)
        {
            await SeedAsync();
        }

        _sender.Delay = TimeSpan.FromMilliseconds(10); // each batch holds its row locks for ~100 ms, so the other processor really overlaps
        var hostA = NewHost(batchSize: 10);
        var hostB = NewHost(batchSize: 10);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // released only once both hosts exist
        var first = Task.Run(async () => { await go.Task; return await DrainAsync(hostA); }, Ct);
        var second = Task.Run(async () => { await go.Task; return await DrainAsync(hostB); }, Ct);
        go.SetResult();
        var claimed = await Task.WhenAll(first, second);

        claimed.ShouldAllBe(count => count > 0, "both processors took part in the race");
        claimed.Sum().ShouldBe(Rows);
        _sender.Sent.Count.ShouldBe(Rows);
        _sender.Sent.Select(m => m.MessageId).Distinct().Count().ShouldBe(Rows);
        await using var scope = _host.Scope();
        (await NotificationTestHost.Db(scope).EmailDeliveries.CountAsync(d => d.Status == EmailDeliveryStatus.Sent, Ct)).ShouldBe(Rows);
    }

    [Fact]
    public async Task A_delivery_that_cannot_render_is_rescheduled_and_does_not_block_the_others()
    {
        var broken = await SeedAsync(template: "no-such-template");
        var other = await SeedAsync();

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(2);

        (await LoadAsync(other.Id)).Status.ShouldBe(EmailDeliveryStatus.Sent);
        var retry = await LoadAsync(broken.Id);
        retry.Status.ShouldBe(EmailDeliveryStatus.Pending);
        retry.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task Failures_are_logged_without_the_address_or_the_body()
    {
        var failing = await SeedAsync(to: "secret.buyer@example.com");
        _sender.RefusedAddresses.Add("secret.buyer@example.com");

        await _host.Processor.ProcessBatchAsync(Ct);

        var log = string.Join('\n', _logs.Lines);
        log.ShouldContain(failing.Id.ToString());
        log.ShouldContain("InvalidOperationException");
        log.ShouldNotContain("secret.buyer");
        log.ShouldNotContain("Xin chào");
        log.ShouldNotContain("TR-0001");
    }

    [Fact]
    public async Task A_send_that_exceeds_the_timeout_is_a_failure_and_the_next_row_is_still_sent()
    {
        await using var host = NewHost(sendTimeout: TimeSpan.FromMilliseconds(200));
        var hanging = await SeedAsync(to: "hangs@example.com");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var next = await SeedAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sender.HangingAddresses.Add("hangs@example.com");

        (await host.Processor.ProcessBatchAsync(Ct)).ShouldBe(2);

        var failed = await LoadAsync(hanging.Id);
        failed.Status.ShouldBe(EmailDeliveryStatus.Pending);
        failed.Attempts.ShouldBe(1);
        failed.LastError.ShouldStartWith("TimeoutException");
        failed.NextAttemptAt.ShouldBeGreaterThan(_clock.GetUtcNow());
        (await LoadAsync(next.Id)).Status.ShouldBe(EmailDeliveryStatus.Sent);
    }

    [Fact]
    public async Task A_row_stays_sent_when_a_later_row_hangs_and_the_processor_is_cancelled()
    {
        var first = await SeedAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await SeedAsync(to: "hangs@example.com");
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sender.HangingAddresses.Add("hangs@example.com");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = _host.Processor.ProcessBatchAsync(stop.Token);
        await _sender.HangStarted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await stop.CancelAsync();

        (await run).ShouldBe(1, "only the first row was handled before the cancellation");
        (await LoadAsync(first.Id)).Status.ShouldBe(EmailDeliveryStatus.Sent);
        var interrupted = await LoadAsync(second.Id);
        interrupted.Status.ShouldBe(EmailDeliveryStatus.Pending);
        interrupted.Attempts.ShouldBe(0, "a row cancelled in flight is not counted as a failure");

        _sender.HangingAddresses.Clear();
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);
        _sender.Sent.Select(m => m.MessageId).ShouldBe([first.Id, second.Id], "nothing is sent twice");
    }

    [Fact]
    public async Task A_row_stays_sent_when_a_later_row_fails()
    {
        var first = await SeedAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = await SeedAsync(to: "refused@example.com");
        _clock.Advance(TimeSpan.FromSeconds(1));
        _sender.RefusedAddresses.Add("refused@example.com");

        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(2);
        _clock.Advance(TimeSpan.FromDays(1));
        _sender.RefusedAddresses.Clear();
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1, "only the failed row is due again");

        (await LoadAsync(first.Id)).Status.ShouldBe(EmailDeliveryStatus.Sent);
        _sender.Sent.Select(m => m.MessageId).ShouldBe([first.Id, second.Id]);
    }

    [Fact]
    public async Task A_notice_written_through_the_writer_is_rendered_and_sent_from_its_stored_model()
    {
        var (userId, address) = await _host.CreateUserAsync(emailConfirmed: true);
        var draft = new NotificationDraft(
            userId, NotificationType.OrderPaid, "Thanh toán thành công", "Vé đã sẵn sàng.", "/account/orders/1", "order-paid:e2e",
            new EmailPlan("order-paid", NotificationTestHost.OrderModel("TR-E2E-0001")));

        (await _host.AddAsync(draft, Ct)).ShouldNotBeNull();
        (await _host.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);

        var message = _sender.Sent.ShouldHaveSingleItem();
        message.To.ShouldBe(address);
        message.Subject.ShouldBe("Thanh toán thành công, đơn hàng TR-E2E-0001");
        message.HtmlBody.ShouldContain("Xin chào Nguyễn Văn Đạt");
        message.HtmlBody.ShouldContain("1.250.000 ₫");
    }

    private static async Task<int> DrainAsync(NotificationTestHost host)
    {
        await using (host)
        {
            var total = 0;
            int claimed;
            while ((claimed = await host.Processor.ProcessBatchAsync(Ct)) > 0)
            {
                total += claimed;
            }

            return total;
        }
    }
}
