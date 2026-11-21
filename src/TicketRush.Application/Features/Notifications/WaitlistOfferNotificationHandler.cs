using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Application.Features.Notifications;

/// <summary>
/// "Có vé cho bạn!" (spec 13 via 09): in-app notice plus the <c>waitlist-offer</c> email, linking to the offer page. Idempotent per
/// offered hold (<c>waitlist-offer:{entry}:{hold}</c>), so a pass to the same entry later is a new notice. The email expires with the
/// offer: sent later it would offer a ticket that has already moved on. A missing user or event is a no-op.
/// </summary>
public sealed partial class WaitlistOfferNotificationHandler(
    IAppDbContext db,
    IUserDirectory users,
    INotificationWriter writer,
    IUserNotifier notifier,
    IOptions<FrontendOptions> frontend,
    TimeProvider timeProvider,
    ILogger<WaitlistOfferNotificationHandler> logger)
    : IIntegrationEventHandler<WaitlistOfferIssued>
{
    public async Task HandleAsync(WaitlistOfferIssued message, IntegrationEventContext context, CancellationToken ct)
    {
        var title = await db.Events.AsNoTracking().Where(e => e.Id == message.EventId).Select(e => e.Title).FirstOrDefaultAsync(ct);
        var contact = await users.GetContactAsync(message.UserId, ct);
        if (title is null || contact is null || message.ExpiresAt <= timeProvider.GetUtcNow())
        {
            return;
        }

        var path = $"/waitlist/offers/{message.EntryId}";
        var expiresText = VietnamTimeText.Format(message.ExpiresAt);
        var (noticeTitle, body) = NotificationCopy.ForWaitlistOffer(title, expiresText, message.ListingId is not null);
        var email = new EmailPlan(
            "waitlist-offer",
            new { name = contact.FullName, eventTitle = title, expiresAtText = expiresText, link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}", offerKind = message.ListingId is null ? "returned" : "resale" },
            ExpiresAt: message.ExpiresAt);
        var draft = new NotificationDraft(
            message.UserId, NotificationType.WaitlistOffer, noticeTitle, body, path, $"waitlist-offer:{message.EntryId}:{message.ListingId ?? message.HoldId}", email);

        if (await writer.AddAsync(draft, ct) is { } notificationId)
        {
            await TryPushAsync(draft, notificationId, ct);
        }
    }

    /// <summary>Best effort, like every notice push: the inbox shows the same data on the next fetch.</summary>
    private async Task TryPushAsync(NotificationDraft draft, Guid notificationId, CancellationToken ct)
    {
        try
        {
            var item = new NotificationItem(notificationId, draft.Type.ToString(), draft.Title, draft.Body, draft.Link, Read: false, timeProvider.GetUtcNow());
            await notifier.PushAsync(draft.UserId, item, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            LogPushFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9005, Level = LogLevel.Warning, Message = "Live push of a waitlist offer failed ({ExceptionType}); the notice itself is saved")]
    private partial void LogPushFailed(string exceptionType);
}
