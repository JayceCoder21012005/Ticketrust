using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Realtime;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>A notice to write and push; with <see cref="Fail"/> the handler throws after it pushed.</summary>
public sealed record NoticeEvent(Guid NotificationId, Guid UserId, bool Fail = false) : IDomainEvent;

/// <summary>Handled by two handlers: the first pushes and succeeds, the second pushes and throws.</summary>
public sealed record TwoHandlerEvent(Guid FirstId, Guid SecondId, Guid UserId) : IDomainEvent;

/// <summary>What the pushes looked like from the outside: who got what, and whether its row was committed when it was sent.</summary>
public sealed class PushProbe
{
    public required string ConnectionString { get; init; }

    public ConcurrentQueue<(Guid Id, bool RowWasCommitted)> Sent { get; } = new();

    /// <summary>Pushes the handlers saw leave while they were still running (inside the batch transaction).</summary>
    public ConcurrentQueue<Guid> SentWhileHandling { get; } = new();

    /// <summary>Makes every send hang: <c>true</c> honours the cancellation token, <c>false</c> ignores it entirely.</summary>
    public bool? Hang { get; set; }

    /// <summary>Cancelled by the first send (a host shutting down while the pushes go out).</summary>
    public CancellationTokenSource? CancelOnSend { get; set; }

    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether the notice row is visible to another connection, i.e. committed.</summary>
    public async Task<bool> IsCommittedAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM notifications WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))! == 1;
    }
}

/// <summary>Writes the notice row in the batch transaction, then pushes it through the real <c>UserNotifier</c>.</summary>
public sealed class NoticeHandler(AppDbContext db, IUserNotifier notifier, PushProbe probe) : IIntegrationEventHandler<NoticeEvent>
{
    public async Task HandleAsync(NoticeEvent message, IntegrationEventContext context, CancellationToken ct)
    {
        await WriteAndPushAsync(db, notifier, probe, message.NotificationId, message.UserId, ct);
        if (message.Fail)
        {
            throw new InvalidOperationException("handler failed after its push");
        }
    }

    public static async Task WriteAndPushAsync(AppDbContext db, IUserNotifier notifier, PushProbe probe, Guid id, Guid userId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO notifications (id, user_id, type, title, body, link, dedupe_key, created_at) VALUES ({id}, {userId}, 'OrderPaid', 't', 'b', NULL, {id.ToString()}, now())",
            ct);
        var sentBefore = probe.Sent.Count;
        await notifier.PushAsync(userId, new NotificationItem(id, "OrderPaid", "t", "b", null, false, DateTimeOffset.UnixEpoch), ct);
        if (probe.Sent.Count > sentBefore)
        {
            probe.SentWhileHandling.Enqueue(id);
        }
    }
}

public sealed class FirstOfTwoHandler(AppDbContext db, IUserNotifier notifier, PushProbe probe) : IIntegrationEventHandler<TwoHandlerEvent>
{
    public Task HandleAsync(TwoHandlerEvent message, IntegrationEventContext context, CancellationToken ct) =>
        NoticeHandler.WriteAndPushAsync(db, notifier, probe, message.FirstId, message.UserId, ct);
}

public sealed class SecondOfTwoHandler(AppDbContext db, IUserNotifier notifier, PushProbe probe) : IIntegrationEventHandler<TwoHandlerEvent>
{
    public async Task HandleAsync(TwoHandlerEvent message, IntegrationEventContext context, CancellationToken ct)
    {
        await NoticeHandler.WriteAndPushAsync(db, notifier, probe, message.SecondId, message.UserId, ct);
        throw new InvalidOperationException("second handler failed");
    }
}

/// <summary>Fails the commit of the outbox batch once, when armed.</summary>
public sealed class FailingCommitInterceptor : DbTransactionInterceptor
{
    public bool Armed { get; set; }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (Armed)
        {
            Armed = false;
            throw new InvalidOperationException("commit failed");
        }

        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }
}

/// <summary>
/// Live pushes leave only after the outbox batch commits (spec 09, final review I2): real Postgres, the real outbox processor and
/// the real <c>UserNotifier</c>, whose hub context is replaced by one that records each send and checks, from another connection,
/// whether the pushed row was already committed.
/// </summary>
public sealed class PostCommitPushTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(NotificationTestHost.Start);
    private readonly FailingCommitInterceptor _commit = new();
    private readonly CapturingLoggerProvider _logs = new();
    private PushProbe _probe = null!;
    private IHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var database = await containers.CreateDatabaseAsync();
        _probe = new PushProbe { ConnectionString = database };
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(TestSettings.For(database, containers.RedisConnectionString));
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton(_probe);
        builder.AddInfrastructure();
        builder.Services.AddApplication();
        builder.Services.AddTicketRushSignalR(builder.Configuration);
        builder.Services.AddSingleton<IHubContext<UserHub>>(new ProbeHubContext(_probe));
        builder.Services.AddIntegrationEventHandler<NoticeEvent, NoticeHandler>();
        builder.Services.AddIntegrationEventHandler<TwoHandlerEvent, FirstOfTwoHandler>();
        builder.Services.AddIntegrationEventHandler<TwoHandlerEvent, SecondOfTwoHandler>();
        builder.Services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_commit));
        builder.Logging.AddProvider(_logs);
        _host = builder.Build();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    private OutboxProcessor Outbox => _host.Services.GetRequiredService<OutboxProcessor>();

    private async Task EnqueueAsync(params IDomainEvent[] events)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var domainEvent in events)
        {
            db.OutboxMessages.Add(new OutboxMessage(Guid.CreateVersion7(), DomainEventTypeRegistry.NameOf(domainEvent.GetType()), DomainEventTypeRegistry.Serialize(domainEvent), null, _clock.GetUtcNow()));
        }

        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_push_leaves_only_after_the_batch_committed()
    {
        var notice = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(notice);

        (await Outbox.ProcessBatchAsync(Ct)).ShouldBe(1);

        _probe.SentWhileHandling.ShouldBeEmpty("nothing reaches a tab while the batch transaction is open");
        _probe.Sent.ShouldHaveSingleItem().ShouldBe((notice.NotificationId, true), "the row exists when its push arrives");
    }

    [Fact]
    public async Task A_handler_that_fails_after_pushing_pushes_nothing_and_the_rest_of_the_batch_still_pushes()
    {
        var failing = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7(), Fail: true);
        var good = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(failing, good);

        (await Outbox.ProcessBatchAsync(Ct)).ShouldBe(2);

        _probe.Sent.Select(s => s.Id).ShouldBe([good.NotificationId]);
        (await _probe.IsCommittedAsync(failing.NotificationId)).ShouldBeFalse("its savepoint was rolled back");
    }

    [Fact]
    public async Task Only_the_failing_handlers_pushes_are_dropped()
    {
        var both = new TwoHandlerEvent(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(both);

        await Outbox.ProcessBatchAsync(Ct);

        // The first handler's savepoint was released: its row commits with the batch (and its receipt), so its push is due.
        _probe.Sent.ShouldHaveSingleItem().ShouldBe((both.FirstId, true));
    }

    [Fact]
    public async Task A_batch_whose_commit_fails_pushes_nothing()
    {
        var notice = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(notice);
        _commit.Armed = true;

        await Should.ThrowAsync<InvalidOperationException>(() => Outbox.ProcessBatchAsync(Ct));

        _probe.Sent.ShouldBeEmpty();
        (await _probe.IsCommittedAsync(notice.NotificationId)).ShouldBeFalse();

        // Redelivered by the next batch, which commits: now it is pushed, once.
        (await Outbox.ProcessBatchAsync(Ct)).ShouldBe(1);
        _probe.Sent.ShouldHaveSingleItem().ShouldBe((notice.NotificationId, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_hanging_hub_neither_holds_the_transaction_open_nor_stalls_the_batch(bool honoursCancellation)
    {
        var notice = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(notice);
        _probe.Hang = honoursCancellation;

        var watch = Stopwatch.StartNew();
        var run = Outbox.ProcessBatchAsync(Ct);
        await _probe.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        (await _probe.IsCommittedAsync(notice.NotificationId)).ShouldBeTrue("the commit happened before the push was awaited");
        (await run.WaitAsync(TimeSpan.FromSeconds(15), Ct)).ShouldBe(1);
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10), "each push is bounded (about 2 s)");
        var log = string.Join('\n', _logs.Lines);
        log.ShouldContain("Live push after the outbox commit failed");
        log.ShouldNotContain(notice.UserId.ToString());
    }

    [Fact]
    public async Task A_shutdown_while_pushing_is_still_honoured_after_the_commit()
    {
        var notice = new NoticeEvent(Guid.CreateVersion7(), Guid.CreateVersion7());
        await EnqueueAsync(notice);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        _probe.CancelOnSend = stop;
        _probe.Hang = true;

        await Should.ThrowAsync<OperationCanceledException>(() => Outbox.ProcessBatchAsync(stop.Token));

        (await _probe.IsCommittedAsync(notice.NotificationId)).ShouldBeTrue("the batch had committed; only the push was cut short");
    }

    [Fact]
    public async Task Outside_an_outbox_batch_a_push_is_sent_at_once()
    {
        var id = Guid.CreateVersion7();

        await _host.Services.GetRequiredService<IUserNotifier>().PushAsync(Guid.CreateVersion7(), new NotificationItem(id, "OrderPaid", "t", "b", null, false, DateTimeOffset.UnixEpoch), Ct);

        _probe.Sent.ShouldHaveSingleItem().Id.ShouldBe(id);
    }

    [Fact]
    public async Task Order_status_pushes_wait_for_the_commit_too()
    {
        // Through the outbox collector directly: an OrderStatusChanged deferred inside a batch is not sent until the batch flushes.
        var collector = OutboxPostCommit.Open();
        try
        {
            await _host.Services.GetRequiredService<IUserNotifier>().PushOrderStatusAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), "Paid", Ct);
            _probe.Sent.ShouldBeEmpty();
            collector.Actions().Count.ShouldBe(1);
        }
        finally
        {
            OutboxPostCommit.Close();
        }
    }

    // ---------------------------------------------------------------- the recording hub context

    private sealed class ProbeHubContext(PushProbe probe) : IHubContext<UserHub>
    {
        public IHubClients Clients { get; } = new ProbeClients(new ProbeProxy(probe));

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class ProbeClients(IClientProxy proxy) : IHubClients
    {
        public IClientProxy All => proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => proxy;

        public IClientProxy Client(string connectionId) => proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => proxy;

        public IClientProxy Group(string groupName) => proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => proxy;

        public IClientProxy User(string userId) => proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => proxy;
    }

    private sealed class ProbeProxy(PushProbe probe) : IClientProxy
    {
        public async Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            probe.SendStarted.TrySetResult();
            if (probe.CancelOnSend is { } stop)
            {
                await stop.CancelAsync();
            }

            if (probe.Hang is true)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else if (probe.Hang is false)
            {
                await new TaskCompletionSource().Task; // never completes, whatever the token says
            }

            if (args is [NotificationItem item])
            {
                probe.Sent.Enqueue((item.Id, await probe.IsCommittedAsync(item.Id)));
            }
            else
            {
                probe.Sent.Enqueue((Guid.Empty, true));
            }
        }
    }
}
