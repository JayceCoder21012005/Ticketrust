using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>orders</c> plus its lines in <c>order_items</c>. <c>xmin</c> is mapped for every aggregate by <see cref="AppDbContext"/>.</summary>
internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>The statuses in which the hold is still reserved for the order (<c>Order.IsOpen</c>).</summary>
    internal const string OpenStatusFilter = "status IN ('Pending','AwaitingPayment','PaymentFailed')";

    /// <summary>Rows the refund worker still owes money for (orders and payment sessions alike).</summary>
    internal const string RefundPendingFilter = "status = 'RefundPending'";

    /// <summary>The refund worker's retry columns (<see cref="RefundTracking"/>), the same on <c>orders</c> and <c>payment_attempts</c>.</summary>
    internal static void MapRefundTracking(ComplexPropertyBuilder<RefundTracking> refund)
    {
        refund.Property(r => r.Attempts).HasColumnName("refund_attempts");
        refund.Property(r => r.NextAttemptAt).HasColumnName("refund_next_attempt_at");
        refund.Property(r => r.LastError).HasColumnName("refund_last_error").HasMaxLength(RefundTracking.MaxErrorLength);
        refund.Property(r => r.NeedsAttention).HasColumnName("refund_needs_attention");
    }

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.RefundReason).HasMaxLength(50);
        builder.ComplexProperty(o => o.Total, total =>
        {
            total.Property(m => m.Amount).HasColumnName("total_amount");
            total.Property(m => m.Currency).HasColumnName("total_currency").HasMaxLength(3);
        });
        builder.ComplexProperty(o => o.Buyer, buyer =>
        {
            buyer.Property(b => b.Name).HasColumnName("buyer_name").HasMaxLength(200);
            buyer.Property(b => b.Email).HasColumnName("buyer_email").HasMaxLength(320);
            buyer.Property(b => b.Phone).HasColumnName("buyer_phone").HasMaxLength(32);
        });
        builder.ComplexProperty(o => o.Refund, MapRefundTracking);
        builder.HasMany(o => o.Items).WithOne().HasForeignKey("OrderId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
        builder.Ignore(o => o.IsOpen);
        builder.Ignore(o => o.IsTerminalForPayment);
        builder.Ignore(o => o.DomainEvents);

        builder.HasOne<Event>().WithMany().HasForeignKey(o => o.EventId).OnDelete(DeleteBehavior.Restrict);

        // C1: one open order per hold. A cancelled, expired, paid or refunded order frees the hold for a new order.
        builder.HasIndex(o => o.HoldId).IsUnique().HasFilter(OpenStatusFilter).HasDatabaseName("ux_orders_open_hold");

        // "My orders", keyset-paged newest first: WHERE user_id = @u AND (created_at, id) < (@c, @i) ORDER BY created_at DESC, id DESC.
        builder.HasIndex(o => new { o.UserId, o.CreatedAt, o.Id }).IsDescending(false, true, true).HasDatabaseName("ix_orders_user_created");

        // OrderExpiryService sweep: open orders whose hold is over.
        builder.HasIndex(o => o.ExpiresAt).HasFilter(OpenStatusFilter).HasDatabaseName("ix_orders_open_expires_at");

        // RefundWorker scan: refunds still owed, oldest (v7 id) first.
        builder.HasIndex(o => o.Id).HasFilter(RefundPendingFilter).HasDatabaseName("ix_orders_refund_pending");
    }
}

/// <summary>
/// Lines of an order. A regular child entity (not owned) because the record's constructor takes <c>Money</c>, which EF
/// can only bind to a complex property. Loaded with the order (auto-include), keyed by a shadow identity column.
/// </summary>
internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items");
        builder.Property<int>("Id").ValueGeneratedOnAdd();
        builder.HasKey("Id");
        builder.Property<Guid>("OrderId");
        builder.ComplexProperty(i => i.UnitPrice, price =>
        {
            price.Property(m => m.Amount).HasColumnName("unit_price_amount");
            price.Property(m => m.Currency).HasColumnName("unit_price_currency").HasMaxLength(3);
        });
    }
}

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Provider).HasMaxLength(50);
        builder.Property(p => p.ProviderSessionId).HasMaxLength(200);
        builder.Property(p => p.RedirectUrl).HasMaxLength(PaymentAttempt.MaxRedirectUrlLength);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.ComplexProperty(p => p.Amount, amount =>
        {
            amount.Property(m => m.Amount).HasColumnName("amount");
            amount.Property(m => m.Currency).HasColumnName("currency").HasMaxLength(3);
        });
        builder.Property<long?>("_refundAmount").HasColumnName("refund_amount");
        builder.Property<string?>("_refundCurrency").HasColumnName("refund_currency").HasMaxLength(3);
        builder.Ignore(p => p.RefundAmount);
        builder.ComplexProperty(p => p.Refund, OrderConfiguration.MapRefundTracking);
        builder.Ignore(p => p.DomainEvents);

        builder.HasOne<Order>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);

        // Webhooks find their order through the gateway's session id.
        builder.HasIndex(p => p.ProviderSessionId).IsUnique().HasDatabaseName("ux_payment_attempts_provider_session_id");

        // RefundWorker scan: session-level refunds still owed, oldest (v7 id) first.
        builder.HasIndex(p => p.Id).HasFilter(OrderConfiguration.RefundPendingFilter).HasDatabaseName("ix_payment_attempts_refund_pending");
    }
}

internal sealed class ProcessedWebhookConfiguration : IEntityTypeConfiguration<ProcessedWebhook>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhook> builder)
    {
        builder.ToTable("processed_webhooks");
        // Event ids are unique per provider only. WebhookInbox's ON CONFLICT targets exactly these columns.
        builder.HasKey(w => new { w.Provider, w.ProviderEventId }).HasName("pk_processed_webhooks");
        builder.Property(w => w.ProviderEventId).HasMaxLength(ProcessedWebhook.MaxProviderEventIdLength).ValueGeneratedNever();
        builder.Property(w => w.Provider).HasMaxLength(50).ValueGeneratedNever();

        // Retention sweeps delete by age.
        builder.HasIndex(w => w.ProcessedAt);
    }
}
