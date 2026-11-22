using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Notifications;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>email_deliveries</c>: emails owed to users, worked off by the delivery service.</summary>
internal sealed class EmailDeliveryConfiguration : IEntityTypeConfiguration<EmailDelivery>
{
    public const string TraceParentProperty = "TraceParent";

    public void Configure(EntityTypeBuilder<EmailDelivery> builder)
    {
        builder.ToTable("email_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.ToAddress).HasMaxLength(EmailDelivery.MaxToAddressLength);
        builder.Property(d => d.Template).HasMaxLength(EmailDelivery.MaxTemplateLength);
        builder.Property(d => d.ModelJson).HasColumnName("model").HasColumnType("jsonb");
        builder.Property(d => d.DedupeKey).HasMaxLength(EmailDelivery.MaxDedupeKeyLength);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.LastError).HasMaxLength(EmailDelivery.MaxErrorLength);

        // Spec 17: the trace of the work that queued the email, so sending it joins that trace. Telemetry, not domain: a shadow property.
        builder.Property<string?>(TraceParentProperty).HasMaxLength(Observability.TraceContext.MaxLength);

        // The default only serves rows written before the column existed (and raw inserts); the entity always sets it.
        builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");

        builder.HasIndex(d => d.DedupeKey).IsUnique().HasDatabaseName("ux_email_deliveries_dedupe_key");

        // Delivery worker scan: pending rows, the one due first up front.
        builder.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_email_deliveries_pending");
    }
}
