namespace TicketRush.Application.Abstractions.Payments;

/// <summary>The <c>processed_webhooks</c> table: one row per provider event the system decided on (spec 08 step 2, C4).</summary>
public interface IWebhookInbox
{
    /// <summary>
    /// Records <paramref name="providerEventId"/> on the caller's current transaction. False when it is already recorded,
    /// i.e. the event is a duplicate. A concurrent delivery of the same event waits until the first one's transaction
    /// ends: if that commits this returns false, if it rolls back (nothing decided) this records the event instead.
    /// </summary>
    Task<bool> TryRecordAsync(string provider, string providerEventId, DateTimeOffset processedAt, CancellationToken ct);
}
