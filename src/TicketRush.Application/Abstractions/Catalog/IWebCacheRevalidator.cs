namespace TicketRush.Application.Abstractions.Catalog;

/// <summary>
/// Evicts the web app's ISR cache entries by tag (spec 04 <c>POST /api/revalidate</c>). Called from outbox handlers, so
/// a failure throws and the message is retried.
/// </summary>
public interface IWebCacheRevalidator
{
    Task RevalidateAsync(IReadOnlyList<string> tags, CancellationToken ct);
}
