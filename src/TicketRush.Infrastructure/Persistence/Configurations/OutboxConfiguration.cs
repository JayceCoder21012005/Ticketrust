using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Infrastructure.Outbox;

namespace TicketRush.Infrastructure.Persistence.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();
        builder.Property(m => m.Type).HasMaxLength(200);
        builder.Property(m => m.Payload).HasColumnType("jsonb");
        builder.Property(m => m.LastError).HasMaxLength(2000);
        builder.Ignore(m => m.IsDeadLettered);

        // Dispatcher scan: only pending rows, in dispatch order.
        builder.HasIndex(m => new { m.NextAttemptAt, m.OccurredAt })
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending");

        // Per-aggregate ordering check ("is there an older pending message for this aggregate?").
        builder.HasIndex(m => new { m.AggregateId, m.OccurredAt })
            .HasFilter("processed_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending_by_aggregate");
    }
}

internal sealed class ConsumerReceiptConfiguration : IEntityTypeConfiguration<ConsumerReceipt>
{
    public void Configure(EntityTypeBuilder<ConsumerReceipt> builder)
    {
        builder.ToTable("consumer_receipts");
        builder.HasKey(r => new { r.MessageId, r.Handler });
        builder.Property(r => r.Handler).HasMaxLength(300);
    }
}
