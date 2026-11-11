using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain.Notifications;

namespace TicketRush.Infrastructure.Persistence.Configurations;

/// <summary>
/// Table <c>event_follows</c>. The key is (user, event); <c>Entity.Id</c> is not a column. The table has no foreign keys
/// (like <c>orders.user_id</c>): a follow of a deleted event or user is harmless and the insert path stays one statement.
/// </summary>
internal sealed class EventFollowConfiguration : IEntityTypeConfiguration<EventFollow>
{
    public void Configure(EntityTypeBuilder<EventFollow> builder)
    {
        builder.ToTable("event_follows");
        builder.Ignore(f => f.Id);
        builder.HasKey(f => new { f.UserId, f.EventId });
        builder.Property(f => f.UserId).ValueGeneratedNever();
        builder.Property(f => f.EventId).ValueGeneratedNever();
    }
}
