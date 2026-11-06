using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Tickets;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table <c>tickets</c>. <c>qr_secret</c> is encrypted by the value converter that <see cref="AppDbContext"/> attaches,
/// because the converter needs the Data Protection provider.
/// </summary>
internal sealed class TicketConfiguration : IEntityTypeConfiguration<Ticket>
{
    /// <summary>The partial unique index that makes a second non-voided ticket for a seat impossible.</summary>
    internal const string SeatIndexName = "ux_tickets_event_seat";

    public void Configure(EntityTypeBuilder<Ticket> builder)
    {
        builder.ToTable("tickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.QrSecret).HasColumnType("bytea");
        builder.Ignore(t => t.DomainEvents);

        builder.HasOne<Order>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Event>().WithMany().HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TicketTier>().WithMany().HasForeignKey(t => t.TierId).OnDelete(DeleteBehavior.Restrict);

        // C1: the final guard against selling a seat twice. A voided ticket (cancelled event, refund) frees its seat.
        builder.HasIndex(t => new { t.EventId, t.SeatIndex })
            .IsUnique()
            .HasFilter("status <> 'Voided' AND seat_index IS NOT NULL")
            .HasDatabaseName(SeatIndexName);

        // "My tickets" (spec 10) and the per-user purchase limit of an event.
        builder.HasIndex(t => t.OwnerUserId);
        builder.HasIndex(t => new { t.EventId, t.OwnerUserId }).HasDatabaseName("ix_tickets_event_owner");
    }
}
