using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Gates;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>gate_assignments</c>. The key is (event, user); <c>Entity.Id</c> is not a column. No foreign keys, like <c>event_follows</c>.</summary>
internal sealed class GateAssignmentConfiguration : IEntityTypeConfiguration<GateAssignment>
{
    public void Configure(EntityTypeBuilder<GateAssignment> builder)
    {
        builder.ToTable("gate_assignments");
        builder.Ignore(a => a.Id);
        builder.HasKey(a => new { a.EventId, a.UserId });
        builder.Property(a => a.EventId).ValueGeneratedNever();
        builder.Property(a => a.UserId).ValueGeneratedNever();
        builder.Property(a => a.GateName).HasMaxLength(GateAssignment.GateNameMaxLength);
        builder.HasIndex(a => a.UserId); // "events assigned to me"
    }
}
