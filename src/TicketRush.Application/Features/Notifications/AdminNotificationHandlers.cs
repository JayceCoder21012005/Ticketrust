using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Users;

namespace TicketRush.Application.Features.Notifications;

/// <summary>
/// Spec 16 via 09: the applicant hears the admin's decision (in-app and the <c>organizer-application</c> email). Idempotent per
/// application. A missing user is a no-op.
/// </summary>
public sealed class OrganizerApplicationNotificationHandler(IUserDirectory users, INotificationWriter writer, IOptions<FrontendOptions> frontend)
    : IIntegrationEventHandler<OrganizerApplicationDecided>
{
    public async Task HandleAsync(OrganizerApplicationDecided message, IntegrationEventContext context, CancellationToken ct)
    {
        if (await users.GetContactAsync(message.UserId, ct) is not { } contact)
        {
            return;
        }

        var approved = message.Decision == OrganizerApplicationStatus.Approved;
        var path = approved ? "/organizer" : "/account";
        var (title, body) = approved
            ? ("Bạn đã trở thành nhà tổ chức", "Đơn đăng ký nhà tổ chức của bạn đã được duyệt. Bạn có thể tạo sự kiện ngay.")
            : ("Đơn đăng ký nhà tổ chức chưa được duyệt", $"Lý do: {message.Reason}");
        var email = new EmailPlan(
            "organizer-application",
            new { name = contact.FullName, decision = approved ? "approved" : "rejected", reason = message.Reason, link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}" });
        await writer.AddAsync(new NotificationDraft(message.UserId, NotificationType.OrganizerApplication, title, body, path, $"organizer-application:{message.ApplicationId}", email), ct);
    }
}

/// <summary>Spec 16 via 09: the organizer hears that an admin suspended (or restored) their event, and why.</summary>
public sealed class EventModerationNotificationHandler(IAppDbContext db, IUserDirectory users, INotificationWriter writer, IOptions<FrontendOptions> frontend)
    : IIntegrationEventHandler<EventSuspended>, IIntegrationEventHandler<EventRestored>
{
    public Task HandleAsync(EventSuspended message, IntegrationEventContext context, CancellationToken ct) =>
        SendAsync(message.EventId, message.OrganizerId, "suspended", message.Reason, context.MessageId, ct);

    public Task HandleAsync(EventRestored message, IntegrationEventContext context, CancellationToken ct) =>
        SendAsync(message.EventId, message.OrganizerId, "restored", message.Reason, context.MessageId, ct);

    private async Task SendAsync(Guid eventId, Guid organizerId, string action, string reason, Guid messageId, CancellationToken ct)
    {
        var eventTitle = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => e.Title).FirstOrDefaultAsync(ct);
        if (eventTitle is null || await users.GetContactAsync(organizerId, ct) is not { } contact)
        {
            return;
        }

        var path = $"/organizer/events/{eventId}/info";
        var (title, body) = action == "suspended"
            ? ($"Sự kiện \"{eventTitle}\" đã bị tạm ẩn", $"Quản trị viên đã tạm ẩn sự kiện. Lý do: {reason}")
            : ($"Sự kiện \"{eventTitle}\" đã được mở lại", $"Quản trị viên đã mở lại sự kiện. Ghi chú: {reason}");
        var email = new EmailPlan("event-moderation", new { name = contact.FullName, eventTitle, action, reason, link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}" });
        await writer.AddAsync(new NotificationDraft(organizerId, NotificationType.EventModeration, title, body, path, $"event-moderation:{eventId}:{messageId}", email), ct);
    }
}
