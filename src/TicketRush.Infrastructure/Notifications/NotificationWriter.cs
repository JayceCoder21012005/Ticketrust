using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>
/// Inserts with <c>ON CONFLICT (dedupe_key) DO NOTHING</c> instead of catching the unique violation: inside the outbox batch
/// transaction a failed statement aborts the whole transaction in Postgres (even a savepoint would have to be rolled back by
/// the caller), whereas a conflict-free insert just reports "0 rows". When there is an email, both rows go in one statement
/// (the delivery selects from the notification insert's <c>RETURNING</c>), so they are atomic with each other even without
/// an enclosing transaction, and the delivery exists exactly when the notification was newly written.
/// </summary>
internal sealed class NotificationWriter(AppDbContext db, IUserDirectory users, TimeProvider timeProvider) : INotificationWriter
{
    public async Task<Guid?> AddAsync(NotificationDraft draft, CancellationToken ct)
    {
        var contact = await users.GetContactAsync(draft.UserId, ct);
        if (contact is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var notification = Notification.Create(draft.UserId, draft.Type, draft.Title, draft.Body, draft.Link, draft.DedupeKey, now);
        var delivery = CanReceiveEmail(contact) ? PlanDelivery(draft, contact, now) : null;

        var written = delivery is null
            ? await InsertNotificationAsync(notification, ct)
            : await InsertNotificationWithDeliveryAsync(notification, delivery, ct);
        return written ? notification.Id : null;
    }

    private static bool CanReceiveEmail(UserContact contact) => contact.EmailConfirmed && !string.IsNullOrWhiteSpace(contact.Email);

    private static EmailDelivery? PlanDelivery(NotificationDraft draft, UserContact contact, DateTimeOffset now) =>
        draft.Email is null
            ? null
            : EmailDelivery.Create(draft.UserId, contact.Email, draft.Email.Template, EmailModelJson.Serialize(draft.Email.Model), draft.DedupeKey, now);

    private async Task<bool> InsertNotificationAsync(Notification n, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO notifications (id, user_id, type, title, body, link, dedupe_key, created_at)
            VALUES ({n.Id}, {n.UserId}, {n.Type.ToString()}, {n.Title}, {n.Body}, {n.Link}, {n.DedupeKey}, {n.CreatedAt})
            ON CONFLICT (dedupe_key) DO NOTHING
            """,
            ct) == 1;

    private async Task<bool> InsertNotificationWithDeliveryAsync(Notification n, EmailDelivery d, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync(
            $"""
            WITH written AS (
                INSERT INTO notifications (id, user_id, type, title, body, link, dedupe_key, created_at)
                VALUES ({n.Id}, {n.UserId}, {n.Type.ToString()}, {n.Title}, {n.Body}, {n.Link}, {n.DedupeKey}, {n.CreatedAt})
                ON CONFLICT (dedupe_key) DO NOTHING
                RETURNING id)
            -- No ON CONFLICT here on purpose: the delivery shares the notification's dedupe key and is inserted only with it, so only a purged notification could make this raise 23505.
            INSERT INTO email_deliveries (id, user_id, to_address, template, model, dedupe_key, status, attempts, next_attempt_at)
            SELECT {d.Id}, {d.UserId}, {d.ToAddress}, {d.Template}, {d.ModelJson}::jsonb, {d.DedupeKey}, {d.Status.ToString()}, {d.Attempts}, {d.NextAttemptAt}
            FROM written
            """,
            ct) == 1;
}
