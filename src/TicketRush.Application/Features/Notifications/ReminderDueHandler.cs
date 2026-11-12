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
/// a user who unfollowed the event after the reminder was planned, an event that is no longer Published, a sale with no opening
/// time left to announce, or a moment that has already passed by the time the message is handled is a no-op, never an error
/// that would hold up other outbox messages.
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
        var draft = await PlanNoticeAsync(message, context, ct);
        if (draft is null)
        {
            return;
        }

        var notificationId = await writer.AddAsync(draft, ct);
        if (notificationId is not null)
        {
            await TryPushAsync(draft, notificationId.Value, ct);
        }
    }

    /// <returns>The notice to write, or <c>null</c> (logged at debug level, without personal data) when there is nothing true left to say.</returns>
    private async Task<NotificationDraft?> PlanNoticeAsync(ReminderDue message, IntegrationEventContext context, CancellationToken ct)
    {
        // The planner only plans for followers, but the outbox can be behind: "Nhắc tôi" turned off since then means no reminder.
        if (!await db.EventFollows.AnyAsync(f => f.UserId == message.UserId && f.EventId == message.EventId, ct))
        {
            return Skipped(message.Kind, "the user no longer follows the event");
        }

        var facts = await LoadEventAsync(message.EventId, context.OccurredAt, ct);
        if (facts is null)
        {
            return Skipped(message.Kind, "event is missing or not published");
        }

        var whenAt = message.Kind == ReminderKind.SaleOpens30m ? facts.NextSaleOpensAt : facts.StartsAt;
        if (whenAt is null)
        {
            return Skipped(message.Kind, "no sale opening left to announce");
        }

        // The outbox can be behind (a backlog, an outage): a reminder for a moment that has come and gone is noise.
        if (whenAt <= timeProvider.GetUtcNow())
        {
            return Skipped(message.Kind, "the moment has already passed");
        }

        var contact = await users.GetContactAsync(message.UserId, ct);
        return contact is null
            ? Skipped(message.Kind, "user is missing")
            : BuildDraft(message, facts, whenAt.Value, contact);
    }

    private NotificationDraft? Skipped(ReminderKind kind, string reason)
    {
        LogSkipped(kind, reason);
        return null;
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

    /// <param name="whenAt">The moment reminded of; the email expires then (sent later it would announce the past).</param>
    private NotificationDraft BuildDraft(ReminderDue message, EventFacts facts, DateTimeOffset whenAt, UserContact contact)
    {
        var sale = message.Kind == ReminderKind.SaleOpens30m;
        var whenText = VietnamTimeText.Format(whenAt);
        var (title, body) = NotificationCopy.ForReminder(message.Kind, facts.Title, whenText);
        var path = $"/events/{facts.Slug}";
        var link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}";
        var email = new EmailPlan(
            sale ? "reminder-sale-opens" : "reminder-event-starts",
            new { name = contact.FullName, link, eventTitle = facts.Title, startsAtText = whenText },
            ExpiresAt: whenAt);

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

    [LoggerMessage(EventId = 9004, Level = LogLevel.Debug, Message = "Skipped a {Kind} reminder: {Reason}")]
    private partial void LogSkipped(ReminderKind kind, string reason);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Warning, Message = "Live push of a {Type} reminder failed ({ExceptionType}); the notice itself is saved")]
    private partial void LogPushFailed(NotificationType type, string exceptionType);
}
