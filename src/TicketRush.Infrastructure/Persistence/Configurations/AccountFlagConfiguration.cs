using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Users;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>Table <c>account_flags</c> (spec 12). At most one open flag per (user, reason): raising it again is a no-op.</summary>
internal sealed class AccountFlagConfiguration : IEntityTypeConfiguration<AccountFlag>
{
    public void Configure(EntityTypeBuilder<AccountFlag> builder)
    {
        builder.ToTable("account_flags");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Reason).HasMaxLength(40);
        builder.Property(f => f.Detail).HasMaxLength(AccountFlag.DetailMaxLength);
        builder.HasIndex(f => new { f.UserId, f.Reason }).IsUnique().HasFilter("resolved_at IS NULL");
        builder.HasIndex(f => f.CreatedAt).HasFilter("resolved_at IS NULL"); // the admin review queue, newest first
    }
}
