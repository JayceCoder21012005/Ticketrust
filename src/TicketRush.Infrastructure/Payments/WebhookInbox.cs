using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> on the caller's transaction: a duplicate is a normal answer, not an error that
/// would abort the transaction. A concurrent insert of the same key waits for the other transaction's outcome (C4).
/// </summary>
public sealed class WebhookInbox(AppDbContext db) : IWebhookInbox
{
    public async Task<bool> TryRecordAsync(string provider, string providerEventId, DateTimeOffset processedAt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerEventId);
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO processed_webhooks (provider, provider_event_id, processed_at)
            VALUES ({provider}, {providerEventId}, {processedAt.ToUniversalTime()})
            ON CONFLICT (provider, provider_event_id) DO NOTHING
            """,
            ct);
        return inserted == 1;
    }
}
