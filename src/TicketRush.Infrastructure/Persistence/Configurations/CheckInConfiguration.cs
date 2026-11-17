using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Gates;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>check_ins</c>: the append-only log of every scan. No foreign keys, so a log line never blocks or slows a scan.</summary>
internal sealed class CheckInConfiguration : IEntityTypeConfiguration<CheckIn>
{
    public void Configure(EntityTypeBuilder<CheckIn> builder)
    {
        builder.ToTable("check_ins");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.GateName).HasMaxLength(GateAssignment.GateNameMaxLength);
        builder.Property(c => c.Result).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.Reason).HasMaxLength(CheckIn.ReasonMaxLength);
        builder.HasIndex(c => new { c.EventId, c.ScannedAt }).HasDatabaseName("ix_check_ins_event_scanned_at"); // the "last minute" counter
        builder.HasIndex(c => c.TicketId).HasDatabaseName("ix_check_ins_ticket");
    }
}
