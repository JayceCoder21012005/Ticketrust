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

    /// <summary>For every holder and follower of the event alike, so it promises a refund only to those who bought.</summary>
    /// <param name="startsAtText">The event's start as <see cref="VietnamTimeText"/> writes it.</param>
    public static (string Title, string Body) ForEventCancelled(string eventTitle, string startsAtText) => (
        "Sự kiện đã bị hủy",
        $"Sự kiện \"{eventTitle}\" dự kiến diễn ra lúc {startsAtText} đã bị hủy. Nếu bạn đã mua vé, tiền sẽ được hoàn lại đầy đủ.");

    /// <summary>For a follower of the event.</summary>
    /// <param name="whenText">The sale's opening time for <see cref="ReminderKind.SaleOpens30m"/>, the event's start for <see cref="ReminderKind.EventStarts24h"/>, as <see cref="VietnamTimeText"/> writes it.</param>
    public static (string Title, string Body) ForReminder(ReminderKind kind, string eventTitle, string whenText) => kind switch
    {
        ReminderKind.SaleOpens30m => (
            "Sắp mở bán vé",
            $"Vé \"{eventTitle}\" sẽ mở bán lúc {whenText}. Hãy chuẩn bị sẵn sàng để không bỏ lỡ."),
        ReminderKind.EventStarts24h => (
            "Sự kiện sắp diễn ra",
            $"Sự kiện \"{eventTitle}\" sẽ diễn ra lúc {whenText}. Hẹn gặp bạn tại sự kiện!"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No reminder has this kind."),
    };

    public static (string Title, string Body) ForWaitlistOffer(string eventTitle, string expiresText) => (
        "Có vé cho bạn!",
        $"Một vé \"{eventTitle}\" vừa được giữ riêng cho bạn từ danh sách chờ. Hãy mua trước {expiresText}, sau đó vé sẽ chuyển cho người tiếp theo.");
}
