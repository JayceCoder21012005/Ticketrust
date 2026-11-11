using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Venues;
using TicketRush.Infrastructure.Notifications;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>A reminder as stored: who is owed what for which event.</summary>
public sealed record Reminder(Guid UserId, Guid EventId, ReminderKind Kind);

/// <summary>
/// Events, follows and a worker-like host for the reminder tests (spec 09): events are built through the domain model at exact
/// offsets from the fake clock, so the window edges can be tested to the second. The outbox rows the setup itself raised
/// (<c>EventPublished</c> ...) are removed, so a drain only ever delivers the reminders under test.
/// </summary>
public sealed class ReminderTestWorld : IAsyncDisposable
{
    private readonly Venue _venue = Venue.Create("Nhà hát nhắc nhở", "1 Đường Thử", "Hà Nội", null, 1_000);
    private int _eventCount;

    private ReminderTestWorld(NotificationTestHost host, FakeTimeProvider clock, ScriptedEmailSender sender, RecordingUserNotifier notifier, CommandRecorder commands)
    {
        Host = host;
        Clock = clock;
        Sender = sender;
        Notifier = notifier;
        Commands = commands;
    }

    public NotificationTestHost Host { get; }

    public FakeTimeProvider Clock { get; }

    public ScriptedEmailSender Sender { get; }

    public RecordingUserNotifier Notifier { get; }

    public CommandRecorder Commands { get; }

    public DateTimeOffset Now => Clock.GetUtcNow();

    public static async Task<ReminderTestWorld> CreateAsync(ContainersFixture containers, CancellationToken ct)
    {
        var clock = new FakeTimeProvider(NotificationTestHost.Start);
        var sender = new ScriptedEmailSender();
        var notifier = new RecordingUserNotifier();
        var commands = new CommandRecorder();
        var host = new NotificationTestHost(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, sender, clock, notifier: notifier, interceptor: commands);
        var world = new ReminderTestWorld(host, clock, sender, notifier, commands);
        await using var scope = host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.Venues.Add(world._venue);
        await db.SaveChangesAsync(ct);
        return world;
    }

    /// <summary>
    /// A new event. With no <paramref name="saleOpens"/> its one sale phase is long over (the sale is not what the test is about);
    /// otherwise one 5-minute phase opens at each given instant, so those must stay before <paramref name="startsAt"/>.
    /// </summary>
    public async Task<Event> EventAsync(EventStatus status, DateTimeOffset startsAt, CancellationToken ct, params DateTimeOffset[] saleOpens)
    {
        var number = Interlocked.Increment(ref _eventCount);
        var evt = Event.CreateDraft(Guid.CreateVersion7(), $"su-kien-nhac-{number}-{Guid.NewGuid():N}", $"Đêm nhạc nhắc nhở {number}", "concert", _venue, startsAt, startsAt.AddHours(3));
        if (saleOpens.Length == 0)
        {
            evt.AddPhase("Mở bán", startsAt.AddDays(-10), startsAt.AddHours(-1), queueEnabled: false, maxTicketsPerUser: 4);
        }

        foreach (var opens in saleOpens)
        {
            evt.AddPhase($"Đợt {opens:HHmm}", opens, opens.AddMinutes(5), queueEnabled: false, maxTicketsPerUser: 4);
        }

        if (status != EventStatus.Draft)
        {
            evt.Publish();
        }

        if (status == EventStatus.Cancelled)
        {
            evt.Cancel();
        }
        else if (status == EventStatus.Ended)
        {
            evt.MarkEnded();
        }

        await using var scope = Host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
        await db.OutboxMessages.Where(m => m.AggregateId == evt.Id).ExecuteDeleteAsync(ct);
        return evt;
    }

    public async Task FollowAsync(Guid userId, Guid eventId, CancellationToken ct)
    {
        await using var scope = Host.Scope();
        var db = NotificationTestHost.Db(scope);
        db.EventFollows.Add(EventFollow.Create(userId, eventId, Clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Bulk follows in one statement (not thousands of entities). The followers have no user rows: the planner does not need them.</summary>
    public async Task FollowManyAsync(Guid eventId, int count, CancellationToken ct)
    {
        await using var scope = Host.Scope();
        var db = NotificationTestHost.Db(scope);
        var now = Clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO event_follows (user_id, event_id, created_at)
            SELECT gen_random_uuid(), {eventId}, {now} FROM generate_series(1, {count})
            """,
            ct);
    }

    public async Task<int> PlanAsync(CancellationToken ct)
    {
        await using var scope = Host.Scope();
        return await scope.ServiceProvider.GetRequiredService<ReminderPlanner>().PlanAsync(Clock.GetUtcNow(), ct);
    }

    public async Task<List<Reminder>> DispatchesAsync(CancellationToken ct)
    {
        await using var scope = Host.Scope();
        return await NotificationTestHost.Db(scope).ReminderDispatches.AsNoTracking()
            .Select(r => new Reminder(r.UserId, r.EventId, r.Kind))
            .ToListAsync(ct);
    }

    public async Task<List<OutboxMessage>> ReminderMessagesAsync(CancellationToken ct)
    {
        await using var scope = Host.Scope();
        var type = DomainEventTypeRegistry.NameOf(typeof(ReminderDue));
        return await NotificationTestHost.Db(scope).OutboxMessages.AsNoTracking().Where(m => m.Type == type).ToListAsync(ct);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();
}
