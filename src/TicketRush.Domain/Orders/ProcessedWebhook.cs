namespace TicketRush.Domain.Orders;

/// <summary>
/// Receipt of a payment webhook the system already decided on. The primary key on (<see cref="Provider"/>,
/// <see cref="ProviderEventId"/>) turns a redelivery into a conflict, which the handler answers with 200 (spec 08, C4);
/// event ids only need to be unique per provider.
/// </summary>
public sealed class ProcessedWebhook(string providerEventId, string provider, DateTimeOffset processedAt)
{
    /// <summary>Column width of <see cref="ProviderEventId"/>; a longer id is refused as an invalid payload.</summary>
    public const int MaxProviderEventIdLength = 200;

    public string ProviderEventId { get; private set; } = providerEventId;

    public string Provider { get; private set; } = provider;

    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
