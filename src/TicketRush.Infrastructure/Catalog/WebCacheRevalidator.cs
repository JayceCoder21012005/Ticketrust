using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Catalog;
using TicketRush.Application.Abstractions.Messaging;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Options;
using TicketRush.Domain.Events;

namespace TicketRush.Infrastructure.Catalog;

public sealed partial class WebCacheRevalidator(HttpClient http, IOptions<FrontendOptions> options, ILogger<WebCacheRevalidator> logger)
    : IWebCacheRevalidator
{
    public const string SecretHeader = "x-revalidate-secret";

    public async Task RevalidateAsync(IReadOnlyList<string> tags, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.RevalidateUrl) || string.IsNullOrWhiteSpace(settings.RevalidateSecret))
        {
            LogDisabled(logger);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.RevalidateUrl)
        {
            Content = JsonContent.Create(new { tags }),
        };
        request.Headers.Add(SecretHeader, settings.RevalidateSecret);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Web cache revalidation is not configured (Frontend:RevalidateUrl/RevalidateSecret); skipped")]
    private static partial void LogDisabled(ILogger logger);
}

/// <summary>
/// Refreshes the public pages of an event after it is published, cancelled or edited (spec 05). Revalidating twice is
/// harmless, so at-least-once delivery needs no extra idempotency.
/// </summary>
internal sealed class CatalogRevalidationHandler(IAppDbContext db, IWebCacheRevalidator revalidator)
    : IIntegrationEventHandler<EventPublished>, IIntegrationEventHandler<EventCancelled>, IIntegrationEventHandler<EventDetailsChanged>,
        IIntegrationEventHandler<EventSuspended>, IIntegrationEventHandler<EventRestored>
{
    public Task HandleAsync(EventSuspended message, IntegrationEventContext context, CancellationToken ct) => RevalidateAsync(message.EventId, ct);

    public Task HandleAsync(EventRestored message, IntegrationEventContext context, CancellationToken ct) => RevalidateAsync(message.EventId, ct);

    public Task HandleAsync(EventPublished message, IntegrationEventContext context, CancellationToken ct) => RevalidateAsync(message.EventId, ct);

    public Task HandleAsync(EventCancelled message, IntegrationEventContext context, CancellationToken ct) => RevalidateAsync(message.EventId, ct);

    public Task HandleAsync(EventDetailsChanged message, IntegrationEventContext context, CancellationToken ct) => RevalidateAsync(message.EventId, ct);

    public static IReadOnlyList<string> TagsFor(Guid eventId, string slug) => ["events:list", $"event:{eventId}", $"event-slug:{slug}"];

    private async Task RevalidateAsync(Guid eventId, CancellationToken ct)
    {
        var slug = await db.Events.AsNoTracking().Where(e => e.Id == eventId).Select(e => e.Slug).SingleOrDefaultAsync(ct);
        if (slug is not null)
        {
            await revalidator.RevalidateAsync(TagsFor(eventId, slug), ct);
        }
    }
}
