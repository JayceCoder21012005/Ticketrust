using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Audit;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table <c>audit_entries</c> (spec 16), append-only: the migration adds a trigger that rejects UPDATE, DELETE and TRUNCATE. Indexed
/// for the admin filters (newest first, by actor, by entity, by action).
/// </summary>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("audit_entries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Action).HasMaxLength(AuditEntry.ActionMaxLength);
        builder.Property(a => a.EntityType).HasMaxLength(64);
        builder.Property(a => a.EntityId).HasMaxLength(128);
        builder.Property(a => a.ActorIp).HasMaxLength(64);
        builder.Property(a => a.CorrelationId).HasMaxLength(128);
        builder.Property(a => a.Changes).HasColumnType("jsonb");
        builder.HasIndex(a => new { a.At, a.Id }).IsDescending(true, true);
        builder.HasIndex(a => new { a.ActorUserId, a.At });
        builder.HasIndex(a => new { a.EntityType, a.EntityId, a.At });
        builder.HasIndex(a => new { a.Action, a.At });
    }
}
