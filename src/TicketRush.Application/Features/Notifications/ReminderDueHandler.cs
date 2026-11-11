using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;

namespace TicketRush.Application.Features.Notifications;

/// <summary>
/// Turns a due reminder into the follower's in-app notice and email (spec 09), then pushes the notice to their open tabs.
/// Idempotent through <c>reminder:{kind}:{eventId}:{userId}</c>: a redelivery writes nothing and pushes nothing. A missing user,
/// an event that is no longer Published, or a sale with no opening time left to announce is a no-op, never an error that would
/// hold up the event's later outbox messages.
/// </summary>
public sealed partial class ReminderDueHandler(
    IAppDbContext db,
    IUserDirectory users,
    INotificationWriter writer,
    IUserNotifier notifier,
    IOptions<FrontendOptions> frontend,
    TimeProvider timeProvider,
    ILogger<ReminderDueHandler> logger)
    : IIntegrationEventHandler<ReminderDue>
{
    private sealed record EventFacts(string Slug, string Title, DateTimeOffset StartsAt, DateTimeOffset? NextSaleOpensAt);

    public async Task HandleAsync(ReminderDue message, IntegrationEventContext context, CancellationToken ct)
    {
        var facts = await LoadEventAsync(message.EventId, context.OccurredAt, ct);
        var whenAt = message.Kind == ReminderKind.SaleOpens30m ? facts?.NextSaleOpensAt : facts?.StartsAt;
        if (facts is null || whenAt is null)
        {
            return;
        }

        var contact = await users.GetContactAsync(message.UserId, ct);
        if (contact is null)
        {
            return;
        }

        var draft = BuildDraft(message, facts, VietnamTimeText.Format(whenAt.Value), contact);
        var notificationId = await writer.AddAsync(draft, ct);
        if (notificationId is not null)
        {
            await TryPushAsync(draft, notificationId.Value, ct);
        }
    }

    /// <summary>
    /// The sale's opening time is the earliest phase opening after the moment the reminder was planned (the message's
    /// <c>OccurredAt</c>), which is the phase the planner saw in its window, whenever the message is handled.
    /// </summary>
    private Task<EventFacts?> LoadEventAsync(Guid eventId, DateTimeOffset plannedAt, CancellationToken ct) =>
        db.Events.AsNoTracking()
            .Where(e => e.Id == eventId && e.Status == EventStatus.Published)
            .Select(e => new EventFacts(e.Slug, e.Title, e.StartsAt, e.Phases.Where(p => p.OpensAt > plannedAt).Min(p => (DateTimeOffset?)p.OpensAt)))
            .SingleOrDefaultAsync(ct);

    private NotificationDraft BuildDraft(ReminderDue message, EventFacts facts, string whenText, UserContact contact)
    {
        var sale = message.Kind == ReminderKind.SaleOpens30m;
        var (title, body) = NotificationCopy.ForReminder(message.Kind, facts.Title, whenText);
        var path = $"/events/{facts.Slug}";
        var link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}";
        var email = new EmailPlan(
            sale ? "reminder-sale-opens" : "reminder-event-starts",
            new { name = contact.FullName, link, eventTitle = facts.Title, startsAtText = whenText });

        return new NotificationDraft(
            message.UserId,
            sale ? NotificationType.SaleOpensSoon : NotificationType.EventStartsSoon,
            title,
            body,
            path,
            $"reminder:{message.Kind}:{message.EventId}:{message.UserId}",
            email);
    }

    /// <summary>
    /// Best effort: a push that fails (the hub or Redis backplane is down) must not undo the notice just written, which would also
    /// stop the event's later outbox handlers. The inbox shows the same data on the next fetch.
    /// </summary>
    private async Task TryPushAsync(NotificationDraft draft, Guid notificationId, CancellationToken ct)
    {
        try
        {
            var item = new NotificationItem(notificationId, draft.Type.ToString(), draft.Title, draft.Body, draft.Link, Read: false, timeProvider.GetUtcNow());
            await notifier.PushAsync(draft.UserId, item, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Type name only: no user, address or text in the log.
            LogPushFailed(draft.Type, ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Live push of a {Type} reminder failed ({ExceptionType}); the notice itself is saved")]
    private partial void LogPushFailed(NotificationType type, string exceptionType);
}
