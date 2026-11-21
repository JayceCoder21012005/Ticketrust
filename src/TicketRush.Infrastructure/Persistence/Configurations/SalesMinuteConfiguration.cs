using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Dashboard;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>sales_per_minute</c> (spec 15): key (event, minute, tier), upserted by the <c>OrderPaid</c> consumer.</summary>
internal sealed class SalesMinuteConfiguration : IEntityTypeConfiguration<SalesMinute>
{
    public void Configure(EntityTypeBuilder<SalesMinute> builder)
    {
        builder.ToTable("sales_per_minute");
        builder.HasKey(s => new { s.EventId, s.Minute, s.TierId });
    }
}
