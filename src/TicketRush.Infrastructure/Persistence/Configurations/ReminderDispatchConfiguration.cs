using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Notifications;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>reminder_dispatches</c>. The key is (user, event, kind); <c>Entity.Id</c> is not a column. <c>xmin</c> is mapped by <see cref="AppDbContext"/> as for every aggregate.</summary>
internal sealed class ReminderDispatchConfiguration : IEntityTypeConfiguration<ReminderDispatch>
{
    public void Configure(EntityTypeBuilder<ReminderDispatch> builder)
    {
        builder.ToTable("reminder_dispatches");
        builder.Ignore(r => r.Id);
        builder.Ignore(r => r.DomainEvents);
        builder.HasKey(r => new { r.UserId, r.EventId, r.Kind });
        builder.Property(r => r.UserId).ValueGeneratedNever();
        builder.Property(r => r.EventId).ValueGeneratedNever();
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20).ValueGeneratedNever();
    }
}
