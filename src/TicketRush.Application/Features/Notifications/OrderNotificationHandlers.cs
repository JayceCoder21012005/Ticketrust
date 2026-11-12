using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Domain.Notifications;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Notifications;

// Spec 09. Each handler is idempotent through the dedupe key of its business entity (not the message id): a replayed or
// re-emitted event writes nothing the second time, so it neither duplicates the notice or the email nor pushes again.
// A missing user or order is a no-op, never an error that would hold up the order's later messages.

/// <summary>Payment confirmed: in-app notice plus the <c>order-paid</c> email (link to the order, no QR).</summary>
public sealed class OrderPaidNotificationHandler(OrderNoticeSender sender) : IIntegrationEventHandler<OrderPaid>
{
    public Task HandleAsync(OrderPaid message, IntegrationEventContext context, CancellationToken ct) =>
        sender.SendAsync(
            new OrderNotice(message.OrderId, NotificationType.OrderPaid, $"order-paid:{message.OrderId}", message.TotalAmount, message.Currency, "order-paid"),
            ct);
}

/// <summary>Money is owed back to the buyer and the refund is on its way.</summary>
public sealed class RefundRequestedNotificationHandler(OrderNoticeSender sender) : IIntegrationEventHandler<RefundRequested>
{
    public Task HandleAsync(RefundRequested message, IntegrationEventContext context, CancellationToken ct) =>
        sender.SendAsync(
            new OrderNotice(message.OrderId, NotificationType.RefundRequested, $"refund-requested:{message.OrderId}", message.Amount, message.Currency, "refund-requested"),
            ct);
}

/// <summary>The gateway confirmed the order's refund.</summary>
public sealed class OrderRefundedNotificationHandler(OrderNoticeSender sender) : IIntegrationEventHandler<OrderRefunded>
{
    public Task HandleAsync(OrderRefunded message, IntegrationEventContext context, CancellationToken ct) =>
        sender.SendAsync(
            new OrderNotice(message.OrderId, NotificationType.Refunded, $"order-refunded:{message.OrderId}", message.Amount, message.Currency, "refunded"),
            ct);
}

/// <summary>The gateway confirmed the refund of one payment session; the buyer is the order's owner.</summary>
public sealed class PaymentRefundedNotificationHandler(OrderNoticeSender sender) : IIntegrationEventHandler<PaymentRefunded>
{
    public Task HandleAsync(PaymentRefunded message, IntegrationEventContext context, CancellationToken ct) =>
        sender.SendAsync(
            new OrderNotice(message.OrderId, NotificationType.Refunded, $"payment-refunded:{message.AttemptId}", message.Amount, message.Currency, "refunded"),
            ct);
}

/// <summary>The hold ran out before payment: in-app only, no email.</summary>
public sealed class OrderExpiredNotificationHandler(OrderNoticeSender sender) : IIntegrationEventHandler<OrderExpired>
{
    public Task HandleAsync(OrderExpired message, IntegrationEventContext context, CancellationToken ct) =>
        sender.SendAsync(
            new OrderNotice(message.OrderId, NotificationType.OrderExpired, $"order-expired:{message.OrderId}", null, Money.DefaultCurrency, null),
            ct);
}
