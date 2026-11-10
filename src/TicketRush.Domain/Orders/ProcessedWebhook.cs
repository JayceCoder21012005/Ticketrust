namespace TicketRush.Domain.Orders;

/// <summary>
/// Receipt of a payment webhook the system already decided on. The primary key on (<see cref="Provider"/>,
/// <see cref="ProviderEventId"/>) turns a redelivery into a conflict, which the handler answers with 200 (spec 08, C4);
/// event ids only need to be unique per provider.
/// </summary>
public sealed class ProcessedWebhook(string providerEventId, string provider, DateTimeOffset processedAt)
{
    /// <summary>The longest event id a provider may send; a longer one is refused as an invalid payload.</summary>
    public const int MaxProviderEventIdLength = 200;

    /// <summary>
    /// Column width of <see cref="ProviderEventId"/>: room for a provider's own id plus the <c>status:</c> prefix of the
    /// receipt a status read is recorded under (<c>provider_session_id</c> is 200 wide, so such a receipt can be 207).
    /// </summary>
    public const int ColumnWidth = 256;

    public string ProviderEventId { get; private set; } = providerEventId;

    public string Provider { get; private set; } = provider;

    public DateTimeOffset ProcessedAt { get; private set; } = processedAt;
}
