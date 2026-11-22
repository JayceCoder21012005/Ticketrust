using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Users;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>organizer_applications</c> (spec 16): one pending application per user.</summary>
internal sealed class OrganizerApplicationConfiguration : IEntityTypeConfiguration<OrganizerApplication>
{
    public void Configure(EntityTypeBuilder<OrganizerApplication> builder)
    {
        builder.ToTable("organizer_applications");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.CompanyName).HasMaxLength(OrganizerApplication.CompanyNameMaxLength);
        builder.Property(a => a.Phone).HasMaxLength(32);
        builder.Property(a => a.Description).HasMaxLength(OrganizerApplication.DescriptionMaxLength);
        builder.Property(a => a.DecisionReason).HasMaxLength(OrganizerApplication.ReasonMaxLength);
        builder.Ignore(a => a.DomainEvents);
        builder.HasIndex(a => a.UserId).IsUnique().HasFilter("status = 'Pending'").HasDatabaseName("ux_organizer_applications_pending_user");
        builder.HasIndex(a => new { a.Status, a.CreatedAt });
    }
}
