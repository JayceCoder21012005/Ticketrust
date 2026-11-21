using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Resale;
using TicketRush.Domain.Tickets;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table <c>resale_listings</c> (spec 14). One open listing (Active or Reserved) per ticket; the public list reads active listings
/// of an event by tier and price. <c>xmin</c> (mapped for every aggregate) makes two reservations of one listing collide.
/// </summary>
internal sealed class ResaleListingConfiguration : IEntityTypeConfiguration<ResaleListing>
{
    public void Configure(EntityTypeBuilder<ResaleListing> builder)
    {
        builder.ToTable("resale_listings");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(16);
        builder.ComplexProperty(l => l.Price, MapMoney("price"));
        builder.Ignore(l => l.DomainEvents);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(l => l.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(l => l.TicketId).IsUnique().HasFilter("status IN ('Active', 'Reserved')").HasDatabaseName("ux_resale_listings_open_ticket");
        builder.HasIndex(l => new { l.EventId, l.TierId }).HasFilter("status = 'Active'");
        builder.HasIndex(l => l.SellerId);
    }

    internal static Action<ComplexPropertyBuilder<Money>> MapMoney(string prefix) => money =>
    {
        money.Property(m => m.Amount).HasColumnName($"{prefix}_amount");
        money.Property(m => m.Currency).HasColumnName($"{prefix}_currency").HasMaxLength(3);
    };
}

/// <summary>Table <c>ticket_transfers</c> (spec 14). One pending transfer per ticket; the sweep reads pending ones by expiry.</summary>
internal sealed class TicketTransferConfiguration : IEntityTypeConfiguration<TicketTransfer>
{
    public void Configure(EntityTypeBuilder<TicketTransfer> builder)
    {
        builder.ToTable("ticket_transfers");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(t => t.ToEmail).HasMaxLength(320);
        builder.Ignore(t => t.DomainEvents);
        builder.HasOne<Ticket>().WithMany().HasForeignKey(t => t.TicketId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(t => t.TicketId).IsUnique().HasFilter("status = 'Pending'").HasDatabaseName("ux_ticket_transfers_pending_ticket");
        builder.HasIndex(t => t.ExpiresAt).HasFilter("status = 'Pending'");
        builder.HasIndex(t => t.EventId);
    }
}

/// <summary>Table <c>seller_payouts</c> (spec 14). One per listing; the mock payout job reads pending ones by event.</summary>
internal sealed class SellerPayoutConfiguration : IEntityTypeConfiguration<SellerPayout>
{
    public void Configure(EntityTypeBuilder<SellerPayout> builder)
    {
        builder.ToTable("seller_payouts");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.ComplexProperty(p => p.Amount, ResaleListingConfiguration.MapMoney("amount"));
        builder.Ignore(p => p.DomainEvents);
        builder.HasOne<ResaleListing>().WithMany().HasForeignKey(p => p.ListingId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.ListingId).IsUnique();
        builder.HasIndex(p => p.EventId).HasFilter("status = 'Pending'");
        builder.HasIndex(p => p.SellerId);
    }
}
