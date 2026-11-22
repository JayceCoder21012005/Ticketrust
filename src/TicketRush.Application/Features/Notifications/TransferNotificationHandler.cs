using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Resale;

namespace TicketRush.Application.Features.Notifications;

/// <summary>
/// Spec 14 via 09: emails the recipient of a transfer the link to <c>/transfers/{id}</c>. The recipient may have no account yet, so
/// this is an email only (no in-app notice). Nothing is sent for a transfer that is no longer pending.
/// </summary>
public sealed class TransferNotificationHandler(
    IAppDbContext db,
    IUserDirectory users,
    IEmailRenderer renderer,
    IEmailSender email,
    IOptions<FrontendOptions> frontend)
    : IIntegrationEventHandler<TransferCreated>
{
    public async Task HandleAsync(TransferCreated message, IntegrationEventContext context, CancellationToken ct)
    {
        var pending = await db.TicketTransfers.AsNoTracking().AnyAsync(t => t.Id == message.TransferId && t.Status == TicketTransferStatus.Pending, ct);
        var evt = await db.Events.AsNoTracking().Where(e => e.Id == message.EventId).Select(e => new { e.Title, e.StartsAt }).FirstOrDefaultAsync(ct);
        var from = await users.GetContactAsync(message.FromUserId, ct);
        if (!pending || evt is null || from is null)
        {
            return;
        }

        var mail = renderer.Render("ticket-transfer", new
        {
            name = message.ToEmail,
            fromName = from.FullName,
            eventTitle = evt.Title,
            startsAtText = VietnamTimeText.Format(evt.StartsAt),
            expiresAtText = VietnamTimeText.Format(message.ExpiresAt),
            link = $"{frontend.Value.BaseUrl.TrimEnd('/')}/transfers/{message.TransferId}",
        });
        await email.SendAsync(mail.ToMessage(context.MessageId, message.ToEmail), ct);
    }
}
