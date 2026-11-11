using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications;

/// <summary>One order event to tell the buyer about. <see cref="DedupeKey"/> is built from the business entity, never from the message.</summary>
/// <param name="Amount">The money the notice names, or <c>null</c> for a notice that names none.</param>
/// <param name="EmailTemplate">Null for an in-app-only notice.</param>
public sealed record OrderNotice(Guid OrderId, NotificationType Type, string DedupeKey, long? Amount, string Currency, string? EmailTemplate);

/// <summary>
/// The shared body of the order notification handlers: loads the order and its event title once, writes the in-app notice
/// (and the email the template asks for) through the writer, then pushes to the buyer's open tabs only when the writer
/// created something. The buyer is the order's owner, so every event resolves its user the same way (the payment-session
/// refund carries no user id).
/// </summary>
public sealed class OrderNoticeSender(
    IAppDbContext db,
    IUserDirectory users,
    INotificationWriter writer,
    IUserNotifier notifier,
    IOptions<FrontendOptions> frontend,
    TimeProvider timeProvider)
{
    private sealed record OrderFacts(Guid UserId, OrderStatus Status, string EventTitle);

    public async Task SendAsync(OrderNotice notice, CancellationToken ct)
    {
        var order = await LoadOrderAsync(notice.OrderId, ct);
        if (order is null)
        {
            return;
        }

        var draft = await BuildDraftAsync(notice, order, ct);
        if (draft is null)
        {
            return;
        }

        var notificationId = await writer.AddAsync(draft, ct);
        if (notificationId is not null)
        {
            await PushAsync(notificationId.Value, draft, notice.OrderId, order.Status, ct);
        }
    }

    private Task<OrderFacts?> LoadOrderAsync(Guid orderId, CancellationToken ct) =>
        (from o in db.Orders.AsNoTracking()
         join e in db.Events.AsNoTracking() on o.EventId equals e.Id
         where o.Id == orderId
         select new OrderFacts(o.UserId, o.Status, e.Title))
        .SingleOrDefaultAsync(ct);

    private async Task<NotificationDraft?> BuildDraftAsync(OrderNotice notice, OrderFacts order, CancellationToken ct)
    {
        var path = $"/account/orders/{notice.OrderId}";
        var amount = notice.Amount is { } value ? FormatMoney(value, notice.Currency) : string.Empty;
        var facts = new OrderNoticeFacts(order.EventTitle, OrderCode(notice.OrderId), amount);
        var (title, body) = NotificationCopy.ForOrder(notice.Type, facts);

        EmailPlan? email = null;
        if (notice.EmailTemplate is not null)
        {
            var contact = await users.GetContactAsync(order.UserId, ct);
            if (contact is null)
            {
                return null;
            }

            var link = $"{frontend.Value.BaseUrl.TrimEnd('/')}{path}";
            email = new EmailPlan(notice.EmailTemplate, new { name = contact.FullName, eventTitle = facts.EventTitle, orderCode = facts.OrderCode, amount = facts.Amount, link });
        }

        return new NotificationDraft(order.UserId, notice.Type, title, body, path, notice.DedupeKey, email);
    }

    private async Task PushAsync(Guid notificationId, NotificationDraft draft, Guid orderId, OrderStatus status, CancellationToken ct)
    {
        var item = new NotificationItem(notificationId, draft.Type.ToString(), draft.Title, draft.Body, draft.Link, Read: false, timeProvider.GetUtcNow());
        await notifier.PushAsync(draft.UserId, item, ct);
        await notifier.PushOrderStatusAsync(draft.UserId, orderId, status.ToString(), ct);
    }

    /// <summary>The same short reference the order page shows the buyer (the web's <c>orderCode</c>).</summary>
    private static string OrderCode(Guid orderId) => $"#{orderId.ToString("D")[..8].ToUpperInvariant()}";

    /// <summary>Whole dong with dots, like the web (<c>1.250.000 ₫</c>); another currency shows its code.</summary>
    private static string FormatMoney(long amount, string currency)
    {
        var digits = amount.ToString("N0", CultureInfo.InvariantCulture).Replace(',', '.');
        return string.Equals(currency, Money.DefaultCurrency, StringComparison.OrdinalIgnoreCase) ? $"{digits} ₫" : $"{digits} {currency}";
    }
}
