namespace TicketRush.Domain.Orders;

/// <summary>
/// Receipt of a payment webhook the system already decided on. The primary key on <see cref="ProviderEventId"/>
/// turns a redelivery into a unique violation, which the handler answers with 200 (spec 08, C4).
/// </summary>
public sealed class ProcessedWebhook(string providerEventId, string provider, DateTimeOffset processedAt)
{
    public string ProviderEventId { get; private set; } = providerEventId;

    public string Provider { get; private set; } = provider;

    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
