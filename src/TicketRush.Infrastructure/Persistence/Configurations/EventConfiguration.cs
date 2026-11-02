using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Venues;

namespace TicketRush.Infrastructure.Persistence.Configurations;

internal sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Slug).HasMaxLength(160);
        builder.HasIndex(e => e.Slug).IsUnique();
        builder.Property(e => e.Title).HasMaxLength(200);
        builder.Property(e => e.Category).HasMaxLength(50);
        builder.Property(e => e.Description).HasMaxLength(20_000);
        builder.Property(e => e.CoverImageUrl).HasMaxLength(500);
        builder.Property(e => e.Lineup).HasColumnType("text[]");
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Venue>().WithMany().HasForeignKey(e => e.VenueId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => e.OrganizerId);
        builder.HasIndex(e => new { e.Status, e.StartsAt, e.Id });

        builder.HasMany(e => e.Phases).WithOne().HasForeignKey(p => p.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(e => e.Tiers).WithOne().HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Phases).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(e => e.Tiers).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Ignore(e => e.DomainEvents);
    }
}

internal sealed class SalePhaseConfiguration : IEntityTypeConfiguration<SalePhase>
{
    public void Configure(EntityTypeBuilder<SalePhase> builder)
    {
        builder.ToTable("sale_phases");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(100);
        builder.Property(p => p.AccessCodeHash).HasMaxLength(128);
        builder.HasIndex(p => new { p.EventId, p.OpensAt });
    }
}

internal sealed class TicketTierConfiguration : IEntityTypeConfiguration<TicketTier>
{
    public void Configure(EntityTypeBuilder<TicketTier> builder)
    {
        builder.ToTable("ticket_tiers");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(100);
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Color).HasMaxLength(20);
        builder.ComplexProperty(t => t.Price, price =>
        {
            price.Property(m => m.Amount).HasColumnName("price_amount");
            price.Property(m => m.Currency).HasColumnName("price_currency").HasMaxLength(3);
        });
    }
}

internal sealed class VenueConfiguration : IEntityTypeConfiguration<Venue>
{
    public void Configure(EntityTypeBuilder<Venue> builder)
    {
        builder.ToTable("venues");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Name).HasMaxLength(200);
        builder.Property(v => v.Address).HasMaxLength(300);
        builder.Property(v => v.City).HasMaxLength(100);
        builder.Property(v => v.MapUrl).HasMaxLength(500);
        builder.Ignore(v => v.DomainEvents);
    }
}
