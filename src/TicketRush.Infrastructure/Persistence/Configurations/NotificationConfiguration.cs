using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Notifications;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>notifications</c>: the in-app inbox. One row per <c>dedupe_key</c>.</summary>
internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(n => n.Title).HasMaxLength(Notification.MaxTitleLength);
        builder.Property(n => n.Body).HasMaxLength(Notification.MaxBodyLength);
        builder.Property(n => n.Link).HasMaxLength(Notification.MaxLinkLength);
        builder.Property(n => n.DedupeKey).HasMaxLength(Notification.MaxDedupeKeyLength);

        // A business entity can notify its user once; the writer swallows the violation.
        builder.HasIndex(n => n.DedupeKey).IsUnique().HasDatabaseName("ux_notifications_dedupe_key");

        // The inbox, keyset-paged newest first: WHERE user_id = @u AND (created_at, id) < (@c, @i) ORDER BY created_at DESC, id DESC.
        builder.HasIndex(n => new { n.UserId, n.CreatedAt, n.Id }).IsDescending(false, true, true).HasDatabaseName("ix_notifications_user_created");

        // The unread badge.
        builder.HasIndex(n => n.UserId).HasFilter("read_at IS NULL").HasDatabaseName("ix_notifications_user_unread");
    }
}
