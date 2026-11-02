using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Api.IntegrationTests.Outbox;

public sealed class OutboxProcessorTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero));
    private readonly Invocations _invocations = new();
    private string _database = null!;

    public async ValueTask InitializeAsync()
    {
        _database = await containers.CreateDatabaseAsync();
        await OutboxTestHost.CreateEffectsTableAsync(_database);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OutboxTestHost NewHost() => new(_database, containers.RedisConnectionString, _invocations, _clock);

    [Fact]
    public async Task Message_committed_before_a_worker_crash_is_handled_exactly_once_after_restart()
    {
        var message = await InsertAsync(OutboxTestHost.Message(new TestEvent(1), _clock.GetUtcNow()));

        // Worker #1 dies in the middle of handling: its batch transaction never commits.
        await using (var crashing = NewHost())
        {
            using var kill = new CancellationTokenSource();
            _invocations.CrashOnNext = kill;
            await Should.ThrowAsync<OperationCanceledException>(() => crashing.Processor.ProcessBatchAsync(kill.Token));
        }

        await using (var restarted = NewHost())
        {
            await restarted.DrainAsync(Ct);
            await restarted.DrainAsync(Ct);
        }

        _invocations.MessageIds.Count.ShouldBe(2, "the crashed attempt ran, then the restart ran it again");
        (await CountEffectsAsync()).ShouldBe(1, "the crashed attempt's writes were rolled back");
        (await LoadAsync(message.Id)).ProcessedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Two_workers_process_10000_messages_without_running_a_handler_twice()
    {
        var now = _clock.GetUtcNow().AddSeconds(-1); // staggered rows must already be due on the frozen clock
        await InsertAsync(Enumerable.Range(0, 10_000).Select(i => OutboxTestHost.Message(new TestEvent(i), now.AddTicks(i * 10))).ToArray());

        await using var workerA = NewHost();
        await using var workerB = NewHost();
        await Task.WhenAll(Task.Run(() => workerA.DrainAsync(Ct), Ct), Task.Run(() => workerB.DrainAsync(Ct), Ct));
        await workerA.DrainAsync(Ct);

        _invocations.MessageIds.Count.ShouldBe(10_000);
        _invocations.MessageIds.Distinct().Count().ShouldBe(10_000);
        (await CountEffectsAsync()).ShouldBe(10_000);
        await using var db = ContainersFixture.CreateDbContext(_database);
        (await db.OutboxMessages.CountAsync(m => m.ProcessedAt == null, Ct)).ShouldBe(0);
        (await db.ConsumerReceipts.CountAsync(Ct)).ShouldBe(10_000);
    }

    [Fact]
    public async Task Always_failing_handler_is_dead_lettered_after_10_attempts_while_others_keep_flowing()
    {
        var now = _clock.GetUtcNow().AddSeconds(-1); // staggered rows must already be due on the frozen clock
        var poison = await InsertAsync(OutboxTestHost.Message(new PoisonEvent(), now));
        await InsertAsync(OutboxTestHost.Message(new TestEvent(1), now.AddTicks(10)));

        await using var worker = NewHost();
        await worker.DrainAsync(Ct);
        _invocations.MessageIds.Count.ShouldBe(1, "a poison message must not block unrelated messages");

        for (var i = 0; i < 12; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(11));
            await worker.DrainAsync(Ct);
        }

        var deadLetter = await LoadAsync(poison.Id);
        deadLetter.Attempts.ShouldBe(10);
        deadLetter.IsDeadLettered.ShouldBeTrue();
        deadLetter.LastError!.ShouldContain("poison");
    }

    [Fact]
    public async Task Failed_message_is_retried_with_backoff_not_immediately()
    {
        await InsertAsync(OutboxTestHost.Message(new PoisonEvent(), _clock.GetUtcNow()));
        await using var worker = NewHost();

        (await worker.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await worker.Processor.ProcessBatchAsync(Ct)).ShouldBe(0, "next attempt is scheduled in the future");

        _clock.Advance(TimeSpan.FromSeconds(1));
        (await worker.Processor.ProcessBatchAsync(Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Messages_of_one_aggregate_are_handled_in_order_even_when_the_first_one_fails()
    {
        var aggregate = Guid.CreateVersion7();
        var now = _clock.GetUtcNow().AddSeconds(-1); // staggered rows must already be due on the frozen clock
        var first = await InsertAsync(OutboxTestHost.Message(new FlakyEvent("first"), now, aggregate));
        await InsertAsync(OutboxTestHost.Message(new FlakyEvent("second"), now.AddTicks(10), aggregate));
        await InsertAsync(OutboxTestHost.Message(new FlakyEvent("other"), now.AddTicks(20), Guid.CreateVersion7()));
        _invocations.FailuresLeft[first.Id] = 1;

        await using var worker = NewHost();
        await worker.DrainAsync(Ct);
        _invocations.Labels.ToArray().ShouldBe(["other"], customMessage: "'second' must wait for 'first'");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await worker.DrainAsync(Ct);
        await worker.DrainAsync(Ct);

        _invocations.Labels.ToArray().ShouldBe(["other", "first", "second"]);
    }

    private async Task<OutboxMessage> InsertAsync(params OutboxMessage[] messages)
    {
        await using var db = ContainersFixture.CreateDbContext(_database);
        db.OutboxMessages.AddRange(messages);
        await db.SaveChangesAsync(Ct);
        return messages[0];
    }

    private async Task<OutboxMessage> LoadAsync(Guid id)
    {
        await using var db = ContainersFixture.CreateDbContext(_database);
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
    }

    private async Task<int> CountEffectsAsync()
    {
        await using var db = ContainersFixture.CreateDbContext(_database);
        return await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM test_effects").SingleAsync(Ct);
    }
}
