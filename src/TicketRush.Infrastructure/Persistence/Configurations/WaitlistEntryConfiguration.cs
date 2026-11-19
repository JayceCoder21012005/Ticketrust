using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Waitlist;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table <c>waitlist_entries</c> (spec 13). One live entry (Waiting or Offered) per (event, user), a FIFO index per event for the
/// waiting line, and an index on offer expiry for the waitlist service. No foreign keys, like <c>event_follows</c>.
/// </summary>
internal sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        builder.HasIndex(e => new { e.EventId, e.UserId }).IsUnique().HasFilter("status IN ('Waiting', 'Offered')");
        builder.HasIndex(e => new { e.EventId, e.QueuedAt }).HasFilter("status = 'Waiting'");
        builder.HasIndex(e => e.OfferExpiresAt).HasFilter("status = 'Offered'");
        builder.HasIndex(e => e.UserId);
    }
}
