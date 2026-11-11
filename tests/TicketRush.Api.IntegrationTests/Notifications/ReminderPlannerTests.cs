extern alias WorkerAssembly;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Locking;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Outbox;
using WorkerAssembly::TicketRush.Worker.Notifications;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>
/// The reminder planner and its scheduler (spec 09): followers of a Published event are owed <c>SaleOpens30m</c> when a phase opens
/// in (now, now+30 min] and <c>EventStarts24h</c> when it starts in (now, now+24 h], exactly once however many planners run, and
/// each new reminder is announced through the outbox in the same transaction. Real Postgres and Redis, fake clock.
/// </summary>
public sealed class ReminderPlannerTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly TimeSpan SaleLead = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StartLead = TimeSpan.FromHours(24);

    private ReminderTestWorld _world = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTimeOffset Now => NotificationTestHost.Start;

    public async ValueTask InitializeAsync() => _world = await ReminderTestWorld.CreateAsync(containers, Ct);

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private static (Guid EventId, ReminderKind Kind) Key(Reminder r) => (r.EventId, r.Kind);

    // ================================================================ tests

    [Fact]
    public async Task Planner_emits_once_per_user_event_kind_across_repeated_runs()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await _world.FollowAsync(first, evt.Id, Ct);
        await _world.FollowAsync(second, evt.Id, Ct);

        (await _world.PlanAsync(Ct)).ShouldBe(4, "two followers, a sale opening soon and a start within a day");
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        (await _world.PlanAsync(Ct)).ShouldBe(0);
        _world.Clock.Advance(TimeSpan.FromMinutes(1));
        (await _world.PlanAsync(Ct)).ShouldBe(0);

        var expected = new[] { first, second }.SelectMany(u => new[] { new Reminder(u, evt.Id, ReminderKind.SaleOpens30m), new Reminder(u, evt.Id, ReminderKind.EventStarts24h) });
        (await _world.DispatchesAsync(Ct)).ShouldBe(expected, ignoreOrder: true);
        var messages = await _world.ReminderMessagesAsync(Ct);
        messages.Count.ShouldBe(4, "one outbox message per new reminder, none for the runs that found nothing new");
    }

    [Fact]
    public async Task Every_new_reminder_is_announced_through_the_outbox_in_the_same_transaction()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(10));
        var follower = Guid.CreateVersion7();
        await _world.FollowAsync(follower, evt.Id, Ct);

        await _world.PlanAsync(Ct);

        var message = (await _world.ReminderMessagesAsync(Ct)).ShouldHaveSingleItem();
        message.Type.ShouldBe("ReminderDue");
        message.AggregateId.ShouldBe(evt.Id);
        message.OccurredAt.ShouldBe(Now);
        message.ProcessedAt.ShouldBeNull();
        message.NextAttemptAt.ShouldBe(Now);
        JsonSerializer.Deserialize<ReminderDue>(message.Payload, DomainEventTypeRegistry.SerializerOptions)
            .ShouldBe(new ReminderDue(follower, evt.Id, ReminderKind.SaleOpens30m));
    }

    [Fact]
    public async Task Two_planners_in_parallel_emit_once()
    {
        const int Followers = 2_000;
        const int Planners = 4;
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        await _world.FollowManyAsync(evt.Id, Followers, Ct);

        // Every planner is ready (scope and planner built) before any starts, so their statements collide on the same keys.
        var scopes = Enumerable.Range(0, Planners).Select(_ => _world.Host.Scope()).ToList();
        var gate = new TaskCompletionSource();
        var runs = scopes.Select(scope => Task.Run(
            async () =>
            {
                var planner = scope.ServiceProvider.GetRequiredService<ReminderPlanner>();
                await gate.Task;
                return await planner.PlanAsync(Now, Ct);
            },
            Ct)).ToArray();
        gate.SetResult();
        var emitted = await Task.WhenAll(runs);
        foreach (var scope in scopes)
        {
            await scope.DisposeAsync();
        }

        emitted.Sum().ShouldBe(Followers * 2, "each (user, kind) was won by exactly one planner");
        var dispatches = await _world.DispatchesAsync(Ct);
        dispatches.Count.ShouldBe(Followers * 2);
        dispatches.Distinct().Count().ShouldBe(Followers * 2);
        var messages = await _world.ReminderMessagesAsync(Ct);
        messages.Count.ShouldBe(Followers * 2, "no reminder was announced twice, none was lost");
        var announced = messages.Select(m => JsonSerializer.Deserialize<ReminderDue>(m.Payload, DomainEventTypeRegistry.SerializerOptions)!).ToList();
        announced.Select(a => (a.UserId, a.Kind)).Distinct().Count().ShouldBe(Followers * 2);
    }

    [Fact]
    public async Task Event_outside_the_window_is_ignored()
    {
        var farSale = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(31));
        var farStart = await _world.EventAsync(EventStatus.Published, Now.AddHours(25), Ct);
        var pastSale = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(-1));
        var pastStart = await _world.EventAsync(EventStatus.Published, Now.AddMinutes(-1), Ct);
        foreach (var evt in new[] { farSale, farStart, pastSale, pastStart })
        {
            await _world.FollowAsync(Guid.CreateVersion7(), evt.Id, Ct);
        }

        (await _world.PlanAsync(Ct)).ShouldBe(0);

        (await _world.DispatchesAsync(Ct)).ShouldBeEmpty();
        (await _world.ReminderMessagesAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_window_excludes_exactly_now_and_includes_exactly_the_lead_time()
    {
        var saleNow = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now);
        var saleEdge = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now + SaleLead);
        var saleJustAfter = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now + SaleLead + TimeSpan.FromSeconds(1));
        var startNow = await _world.EventAsync(EventStatus.Published, Now, Ct);
        var startEdge = await _world.EventAsync(EventStatus.Published, Now + StartLead, Ct);
        var startJustAfter = await _world.EventAsync(EventStatus.Published, Now + StartLead + TimeSpan.FromSeconds(1), Ct);
        foreach (var evt in new[] { saleNow, saleEdge, saleJustAfter, startNow, startEdge, startJustAfter })
        {
            await _world.FollowAsync(Guid.CreateVersion7(), evt.Id, Ct);
        }

        (await _world.PlanAsync(Ct)).ShouldBe(2);

        (await _world.DispatchesAsync(Ct)).Select(Key).ShouldBe(
            [(saleEdge.Id, ReminderKind.SaleOpens30m), (startEdge.Id, ReminderKind.EventStarts24h)],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Draft_cancelled_or_ended_events_are_ignored()
    {
        var draft = await _world.EventAsync(EventStatus.Draft, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var cancelled = await _world.EventAsync(EventStatus.Cancelled, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var ended = await _world.EventAsync(EventStatus.Ended, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var published = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var follower = Guid.CreateVersion7();
        foreach (var evt in new[] { draft, cancelled, ended, published })
        {
            await _world.FollowAsync(follower, evt.Id, Ct);
        }

        (await _world.PlanAsync(Ct)).ShouldBe(2, "only the Published event is reminded about, for both kinds");

        (await _world.DispatchesAsync(Ct)).Select(r => r.EventId).Distinct().ShouldBe([published.Id]);
    }

    [Fact]
    public async Task Unfollowed_user_gets_nothing()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        var other = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct);
        var follower = Guid.CreateVersion7();
        var stranger = Guid.CreateVersion7();
        await _world.FollowAsync(follower, evt.Id, Ct);
        await _world.FollowAsync(stranger, other.Id, Ct);

        (await _world.PlanAsync(Ct)).ShouldBe(2);

        (await _world.DispatchesAsync(Ct)).ShouldAllBe(r => r.UserId == follower, "someone who follows another event, or none, is not reminded");
    }

    [Fact]
    public async Task Follow_created_inside_the_window_is_reminded_on_the_next_tick()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(20));
        var early = Guid.CreateVersion7();
        var late = Guid.CreateVersion7();
        await _world.FollowAsync(early, evt.Id, Ct);
        (await _world.PlanAsync(Ct)).ShouldBe(1);

        _world.Clock.Advance(TimeSpan.FromMinutes(5));
        await _world.FollowAsync(late, evt.Id, Ct);

        (await _world.PlanAsync(Ct)).ShouldBe(1, "only the new follower; the first was already reminded");
        (await _world.DispatchesAsync(Ct)).Select(r => r.UserId).ShouldBe([early, late], ignoreOrder: true);
        (await _world.ReminderMessagesAsync(Ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_event_with_two_phases_opening_in_the_window_emits_one_sale_reminder_per_user()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddDays(10), Ct, Now.AddMinutes(10), Now.AddMinutes(20));
        var follower = Guid.CreateVersion7();
        await _world.FollowAsync(follower, evt.Id, Ct);

        (await _world.PlanAsync(Ct)).ShouldBe(1);
        _world.Clock.Advance(TimeSpan.FromMinutes(12));
        (await _world.PlanAsync(Ct)).ShouldBe(0, "the second phase opening later does not earn a second reminder");

        (await _world.DispatchesAsync(Ct)).ShouldBe([new Reminder(follower, evt.Id, ReminderKind.SaleOpens30m)]);
        (await _world.ReminderMessagesAsync(Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_number_of_statements_does_not_depend_on_the_number_of_followers()
    {
        var small = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        await _world.FollowManyAsync(small.Id, 1, Ct);
        _world.Commands.Clear();
        (await _world.PlanAsync(Ct)).ShouldBe(2);
        var forOne = _world.Commands.Commands.Count;

        var large = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        await _world.FollowManyAsync(large.Id, 5_000, Ct);
        _world.Commands.Clear();
        (await _world.PlanAsync(Ct)).ShouldBe(10_000);
        var forMany = _world.Commands.Commands.Count;

        forMany.ShouldBe(forOne);
        forMany.ShouldBeLessThanOrEqualTo(3, string.Join("\n---\n", _world.Commands.Commands));
        (await _world.ReminderMessagesAsync(Ct)).Count.ShouldBe(10_002);
    }

    [Fact]
    public async Task Scheduler_runs_only_on_the_leader()
    {
        var evt = await _world.EventAsync(EventStatus.Published, Now.AddHours(12), Ct, Now.AddMinutes(10));
        await _world.FollowManyAsync(evt.Id, 3, Ct);
        var scopes = _world.Host.Services.GetRequiredService<IServiceScopeFactory>();
        var locks = _world.Host.Services.GetRequiredService<IDistributedLock>();
        var a = new ReminderSchedulerService(scopes, locks, _world.Clock, NullLogger<ReminderSchedulerService>.Instance);
        var b = new ReminderSchedulerService(scopes, locks, _world.Clock, NullLogger<ReminderSchedulerService>.Instance);

        await a.StartAsync(Ct);
        await b.StartAsync(Ct);
        try
        {
            await WaitUntilAsync(() => a.TickCount + b.TickCount >= 1, "a leader ran a tick");
            var (leader, follower) = a.TickCount >= b.TickCount ? (a, b) : (b, a);

            // The leader ticks again every minute; the follower retries for the lock every few seconds and never gets it.
            // The clock is advanced until the tick happens (not once): the leader may not have reached its delay yet.
            for (var tick = 2; tick <= 4; tick++)
            {
                await WaitUntilAsync(
                    () =>
                    {
                        _world.Clock.Advance(TimeSpan.FromMinutes(1));
                        return leader.TickCount >= tick;
                    },
                    $"the leader's tick {tick}");
            }

            follower.TickCount.ShouldBe(0);
            (await _world.DispatchesAsync(Ct)).Count.ShouldBe(6, "3 followers x 2 kinds, written once");
            (await _world.ReminderMessagesAsync(Ct)).Count.ShouldBe(6);
        }
        finally
        {
            await a.StopAsync(CancellationToken.None);
            await b.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Not met in time: {what}");
            }

            await Task.Delay(50, CancellationToken.None);
        }
    }
}
