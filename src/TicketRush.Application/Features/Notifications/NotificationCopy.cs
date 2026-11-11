using TicketRush.Domain.Notifications;

namespace TicketRush.Application.Features.Notifications;

/// <summary>What an order notice says: the facts it is built from, resolved once per event.</summary>
/// <param name="OrderCode">The short reference the buyer sees on the order page (<c>#</c> and the first 8 characters of the id).</param>
/// <param name="Amount">Already formatted (<c>1.250.000 ₫</c>); empty for a notice that names no money.</param>
public sealed record OrderNoticeFacts(string EventTitle, string OrderCode, string Amount);

/// <summary>The Vietnamese titles and bodies of the in-app order notices. Emails have their own wording in their templates.</summary>
public static class NotificationCopy
{
    public static (string Title, string Body) ForOrder(NotificationType type, OrderNoticeFacts facts) => type switch
    {
        NotificationType.OrderPaid => (
            "Thanh toán thành công",
            $"Đơn {facts.OrderCode} cho \"{facts.EventTitle}\" đã được thanh toán ({facts.Amount}). Vé của bạn đã sẵn sàng."),
        NotificationType.RefundRequested => (
            "Đang hoàn tiền cho đơn hàng",
            $"Chúng tôi đang hoàn {facts.Amount} cho đơn {facts.OrderCode} (\"{facts.EventTitle}\"). Bạn sẽ nhận thông báo khi hoàn tất."),
        NotificationType.Refunded => (
            "Đã hoàn tiền",
            $"Đã hoàn {facts.Amount} cho đơn {facts.OrderCode} (\"{facts.EventTitle}\"). Tiền về phương thức thanh toán của bạn trong ít ngày."),
        NotificationType.OrderExpired => (
            "Đơn hàng đã hết hạn",
            $"Đơn {facts.OrderCode} cho \"{facts.EventTitle}\" đã hết hạn giữ chỗ vì chưa được thanh toán. Bạn có thể đặt lại nếu còn vé."),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "No order notice has this type."),
    };
}
