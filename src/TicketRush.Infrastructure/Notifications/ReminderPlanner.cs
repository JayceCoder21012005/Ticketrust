using Microsoft.EntityFrameworkCore;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Outbox;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>What the scheduler needs from the planner (so the scheduler's leader election can be tested with a planner that cannot fail).</summary>
public interface IReminderPlanner
{
    /// <returns>The number of reminders this call recorded.</returns>
    Task<int> PlanAsync(DateTimeOffset now, CancellationToken ct);
}

/// <summary>
/// One scheduler tick (spec 09): records the reminders the followers of Published events are now owed, and announces each
/// through the outbox. <see cref="ReminderKind.SaleOpens30m"/> when a phase of the event opens in (now, now + 30 min],
/// <see cref="ReminderKind.EventStarts24h"/> when the event starts in (now, now + 24 h]. An event with several phases opening in
/// the window earns one sale reminder per follower (the key is (user, event, kind)).
/// <para>
/// Atomic and safe to run twice at once. The inserts go straight into <c>reminder_dispatches</c> with
/// <c>ON CONFLICT DO NOTHING RETURNING</c>, so only the planner that actually inserted a row learns of it, and only for those
/// rows does it write the <see cref="OutboxMessage"/> (the raw insert bypasses the aggregate, so the outbox interceptor cannot
/// do it). Rows and messages commit together: a crash between them leaves neither, and the next tick plans again. Set-based: two
/// statements however many followers there are. The rows are inserted in key order, so two planners that collide wait for each
/// other instead of deadlocking.
/// </para>
/// <para>
/// The outbox rows deliberately have a <c>NULL</c> aggregate id. The dispatcher only claims a message when no older pending
/// message of the same aggregate exists (<c>older.aggregate_id = m.aggregate_id</c>), so rows sharing the event's id would be
/// claimed one per batch, each claim scanning the other pending reminders of that event: a fan-out to thousands of followers
/// would crawl. Reminders need no ordering among themselves or against the event's other messages, and a <c>NULL</c> never
/// equals anything, so every row is claimable at once.
/// </para>
/// </summary>
public sealed class ReminderPlanner(AppDbContext db) : IReminderPlanner
{
    /// <summary>How far ahead of a sale opening its followers are reminded.</summary>
    public static readonly TimeSpan SaleLead = TimeSpan.FromMinutes(30);

    /// <summary>How far ahead of an event's start its followers are reminded.</summary>
    public static readonly TimeSpan StartLead = TimeSpan.FromHours(24);

    private sealed record Planned(Guid UserId, Guid EventId, string Kind);

    public async Task<int> PlanAsync(DateTimeOffset now, CancellationToken ct)
    {
        now = now.ToUniversalTime();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var planned = await InsertDispatchesAsync(now, ct);
        if (planned.Count > 0)
        {
            await InsertOutboxMessagesAsync(planned, now, ct);
        }

        await transaction.CommitAsync(ct);
        return planned.Count;
    }

    // The context's snake_case convention maps the returned columns to Planned's properties.
    private Task<List<Planned>> InsertDispatchesAsync(DateTimeOffset now, CancellationToken ct) =>
        db.Database.SqlQuery<Planned>(DispatchSql(now)).ToListAsync(ct);

    private static FormattableString DispatchSql(DateTimeOffset now)
    {
        var saleKind = nameof(ReminderKind.SaleOpens30m);
        var startKind = nameof(ReminderKind.EventStarts24h);
        var published = nameof(EventStatus.Published);
        var saleUntil = now + SaleLead;
        var startUntil = now + StartLead;

        return $"""
            WITH due AS (
                SELECT e.id AS event_id, {saleKind} AS kind
                FROM events e
                WHERE e.status = {published}
                  AND EXISTS (SELECT 1 FROM sale_phases p WHERE p.event_id = e.id AND p.opens_at > {now} AND p.opens_at <= {saleUntil})
                UNION ALL
                SELECT e.id, {startKind}
                FROM events e
                WHERE e.status = {published} AND e.starts_at > {now} AND e.starts_at <= {startUntil}),
            inserted AS (
                INSERT INTO reminder_dispatches (user_id, event_id, kind, created_at)
                SELECT f.user_id, f.event_id, d.kind, {now}
                FROM due d
                JOIN event_follows f ON f.event_id = d.event_id
                ORDER BY f.user_id, f.event_id, d.kind
                ON CONFLICT DO NOTHING
                RETURNING user_id, event_id, kind)
            SELECT user_id, event_id, kind FROM inserted
            """;
    }

    /// <summary>One multi-row insert from parallel arrays, so the statement count does not grow with the recipients.</summary>
    private async Task InsertOutboxMessagesAsync(List<Planned> planned, DateTimeOffset now, CancellationToken ct)
    {
        var messages = planned.ConvertAll(row => new OutboxMessage(
            Guid.CreateVersion7(),
            DomainEventTypeRegistry.NameOf(typeof(ReminderDue)),
            DomainEventTypeRegistry.Serialize(new ReminderDue(row.UserId, row.EventId, Enum.Parse<ReminderKind>(row.Kind))),
            aggregateId: null,
            now));
        var ids = messages.ConvertAll(m => m.Id).ToArray();
        var types = messages.ConvertAll(m => m.Type).ToArray();
        var payloads = messages.ConvertAll(m => m.Payload).ToArray();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO outbox_messages (id, type, payload, aggregate_id, occurred_at, attempts, next_attempt_at)
            SELECT m.id, m.type, m.payload::jsonb, NULL, {now}, 0, {now}
            FROM unnest({ids}, {types}, {payloads}) AS m(id, type, payload)
            """,
            ct);
    }
}
