using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Features.Notifications;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>
/// Tells everyone with a stake in a cancelled event (spec 09): the users of its Paid, RefundPending and Refunded orders (the
/// inventory handler, which runs first, has by now moved the Paid ones to RefundPending) and its followers, once each.
/// <para>
/// Set-based, because an event can have thousands of holders: after the event lookup, ONE statement writes the notices, and, from
/// the rows that statement newly inserted (<c>RETURNING</c>), the emails of those with a confirmed address. So the cost is the
/// same two statements for ten recipients or ten thousand, and a notice and its email stand or fall together. The dedupe key
/// <c>event-cancelled:{eventId}:{userId}</c> makes a holder who also follows one notice, and a redelivery (or a re-emitted event)
/// write nothing, push nothing. The handler's context joins the outbox batch transaction.
/// </para>
/// </summary>
internal sealed partial class EventCancelledNotificationHandler(
    AppDbContext db,
    IUserNotifier notifier,
    IOptions<FrontendOptions> frontend,
    TimeProvider timeProvider,
    ILogger<EventCancelledNotificationHandler> logger)
    : IIntegrationEventHandler<EventCancelled>
{
    /// <summary>Most live pushes in flight at once.</summary>
    public const int PushChunkSize = 500;

    private static readonly string[] HolderStatuses =
        [nameof(OrderStatus.Paid), nameof(OrderStatus.RefundPending), nameof(OrderStatus.Refunded)];

    private sealed record EventFacts(string Slug, string Title, DateTimeOffset StartsAt);

    /// <summary>A notice this run wrote: what the live push needs on top of the shared text.</summary>
    private sealed record Written(Guid Id, Guid UserId, DateTimeOffset CreatedAt);

    public async Task HandleAsync(EventCancelled message, IntegrationEventContext context, CancellationToken ct)
    {
        var evt = await db.Events.AsNoTracking()
            .Where(e => e.Id == message.EventId)
            .Select(e => new EventFacts(e.Slug, e.Title, e.StartsAt))
            .SingleOrDefaultAsync(ct);
        if (evt is null)
        {
            return;
        }

        var startsAtText = VietnamTimeText.Format(evt.StartsAt);
        var (title, body) = NotificationCopy.ForEventCancelled(evt.Title, startsAtText);

        // Built through the entity so the text and the link pass the same rules as every other notice; the row shape is shared.
        var shape = Notification.Create(Guid.CreateVersion7(), NotificationType.EventCancelled, title, body, $"/events/{evt.Slug}", $"event-cancelled:{message.EventId}:shape", timeProvider.GetUtcNow());
        var written = await WriteAsync(message.EventId, shape, evt, startsAtText, ct);
        await PushAsync(written, shape, ct);
    }

    private async Task<List<Written>> WriteAsync(Guid eventId, Notification shape, EventFacts evt, string startsAtText, CancellationToken ct)
    {
        var type = shape.Type.ToString();
        var keyPrefix = $"event-cancelled:{eventId}:";
        var url = $"{frontend.Value.BaseUrl.TrimEnd('/')}{shape.Link}";
        var pending = nameof(EmailDeliveryStatus.Pending);

        // The context's snake_case convention maps the returned columns to Written's properties.
        return await db.Database.SqlQuery<Written>(
            $"""
            WITH recipients AS (
                SELECT user_id FROM orders WHERE event_id = {eventId} AND status = ANY({HolderStatuses})
                UNION
                SELECT user_id FROM event_follows WHERE event_id = {eventId}),
            written AS (
                INSERT INTO notifications (id, user_id, type, title, body, link, dedupe_key, created_at)
                SELECT gen_random_uuid(), u.id, {type}, {shape.Title}, {shape.Body}, {shape.Link}, {keyPrefix} || u.id::text, {shape.CreatedAt}
                FROM recipients r
                JOIN users u ON u.id = r.user_id
                ON CONFLICT (dedupe_key) DO NOTHING
                RETURNING id, user_id, created_at, dedupe_key),
            mail AS (
                -- No expires_at: a cancellation is worth telling however late.
                INSERT INTO email_deliveries (id, user_id, to_address, template, model, dedupe_key, status, attempts, next_attempt_at, created_at)
                SELECT gen_random_uuid(), u.id, u.email, 'event-cancelled',
                       jsonb_build_object('name', u.full_name, 'link', {url}, 'eventTitle', {evt.Title}, 'startsAtText', {startsAtText}),
                       w.dedupe_key, {pending}, 0, {shape.CreatedAt}, {shape.CreatedAt}
                FROM written w
                JOIN users u ON u.id = w.user_id
                WHERE u.email_confirmed AND btrim(coalesce(u.email, '')) <> ''
                ON CONFLICT (dedupe_key) DO NOTHING)
            SELECT id, user_id, created_at FROM written
            """).ToListAsync(ct);
    }

    /// <summary>
    /// Best effort, in chunks so thousands of recipients are not pushed at once: a hub that is down must not undo the notices just
    /// written (which would also hold back the event's later outbox handlers). The inbox and the polling fallback show the same data.
    /// </summary>
    private async Task PushAsync(List<Written> written, Notification shape, CancellationToken ct)
    {
        foreach (var chunk in written.Chunk(PushChunkSize))
        {
            await Task.WhenAll(chunk.Select(row =>
            {
                var item = new NotificationItem(row.Id, shape.Type.ToString(), shape.Title, shape.Body, shape.Link, Read: false, row.CreatedAt);
                return TryPushAsync(row.UserId, item, ct);
            }));
        }
    }

    private async Task TryPushAsync(Guid userId, NotificationItem item, CancellationToken ct)
    {
        try
        {
            await notifier.PushAsync(userId, item, ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Type name only: no user, address or text in the log.
            LogPushFailed(ex.GetType().Name);
        }
    }

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "Live push of an event-cancelled notice failed ({ExceptionType}); the notice itself is saved")]
    private partial void LogPushFailed(string exceptionType);
}
