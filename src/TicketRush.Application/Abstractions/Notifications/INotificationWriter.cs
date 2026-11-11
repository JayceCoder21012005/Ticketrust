using TicketRush.Domain.Notifications;

namespace TicketRush.Application.Abstractions.Notifications;

/// <summary>An email to queue with a notification: the template name and its flat model of camelCase string members.</summary>
public sealed record EmailPlan(string Template, object Model);

/// <param name="DedupeKey">Built from the business entity (<c>order-paid:{orderId}</c>), never from a message id; the notification and its email share it.</param>
/// <param name="Email">Null for an in-app-only notice.</param>
public sealed record NotificationDraft(Guid UserId, NotificationType Type, string Title, string Body, string? Link, string DedupeKey, EmailPlan? Email);

/// <summary>
/// Writes a user's in-app notice and, when the draft asks for one, the email they are owed. Saves through the handler's scoped
/// context, so with an outbox handler both rows commit with its consumer receipt. Handlers never talk to SMTP.
/// </summary>
public interface INotificationWriter
{
    /// <summary>
    /// The email is queued only when the user has a confirmed email address; otherwise only the in-app row is written.
    /// Both rows are written together or not at all.
    /// </summary>
    /// <returns>
    /// The notification id, or <c>null</c> when nothing was written: the <c>DedupeKey</c> was already used (a replayed or re-emitted
    /// event) or the user does not exist. Callers push to the user only for a non-null id.
    /// </returns>
    Task<Guid?> AddAsync(NotificationDraft draft, CancellationToken ct);
}
