using Microsoft.EntityFrameworkCore;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Outbox;

/// <summary>How far behind the outbox is (spec 17): the age of the oldest message that is due and the dead letters nobody handled.</summary>
public sealed class OutboxStats(AppDbContext db, TimeProvider time)
{
    public async Task<(double LagSeconds, long DeadLetters)> ReadAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var oldestDue = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null && m.NextAttemptAt <= now)
            .MinAsync(m => (DateTimeOffset?)m.OccurredAt, ct);
        var dead = await db.OutboxMessages
            .LongCountAsync(m => m.ProcessedAt != null && m.LastError != null && m.DiscardedAt == null, ct);
        return (oldestDue is { } oldest ? Math.Max(0, (now - oldest).TotalSeconds) : 0, dead);
    }
}
