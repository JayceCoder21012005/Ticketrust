using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Delivers the mock gateway's queued webhooks to the api, like a real provider's retrying sender. Each attempt is
/// signed with the current time (a webhook delayed by 3 minutes, or retried, would otherwise outlive the 5 minute replay
/// window). A non-2xx answer or a network error reschedules it with exponential backoff; the endpoint not existing yet
/// is just another failure, so a worker started before the api (or before the webhook endpoint is built) never crashes.
/// </summary>
public sealed partial class MockWebhookDeliverer(
    MockWebhookQueue queue,
    HttpClient http,
    IOptionsMonitor<MockGatewayOptions> options,
    TimeProvider time,
    ILogger<MockWebhookDeliverer> logger)
{
    public const int BatchSize = 50;

    public static readonly System.Diagnostics.ActivitySource ActivitySource = new("TicketRush.MockGateway");

    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    /// <summary>One pass: sends every delivery that is due. Returns how many the receiver accepted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var accepted = 0;
        foreach (var claimed in await queue.ClaimDueAsync(time.GetUtcNow(), BatchSize, Lease, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (await TrySendAsync(claimed.Delivery, ct))
            {
                await queue.AcknowledgeAsync(claimed, ct);
                accepted++;
            }
            else
            {
                await FailAsync(claimed, ct);
            }
        }

        return accepted;
    }

    private async Task<bool> TrySendAsync(MockDelivery delivery, CancellationToken ct)
    {
        // The HttpClient instrumentation sends this span's traceparent, so the webhook request joins the purchase's trace.
        using var activity = ActivitySource.StartActivity("mock-gateway deliver", System.Diagnostics.ActivityKind.Producer, Observability.TraceContext.ParentOf(delivery.TraceParent));
        var current = options.CurrentValue;
        using var request = new HttpRequestMessage(HttpMethod.Post, current.WebhookUrl)
        {
            Content = new StringContent(delivery.Body, new UTF8Encoding(false), new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.TryAddWithoutValidation(WebhookHeaders.SignatureHeader, WebhookSignature.Sign(current.WebhookSecret, delivery.Body, time.GetUtcNow()));
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogRejected(logger, delivery.DeliveryId, (int)response.StatusCode, delivery.Attempt + 1);
            }

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreachable(logger, delivery.DeliveryId, delivery.Attempt + 1, ex);
            return false;
        }
    }

    private async Task FailAsync(ClaimedDelivery claimed, CancellationToken ct)
    {
        if (claimed.Delivery.Attempt + 1 >= options.CurrentValue.MaxDeliveryAttempts)
        {
            LogDropped(logger, claimed.Delivery.DeliveryId);
            await queue.AcknowledgeAsync(claimed, ct);
            return;
        }

        var backoff = TimeSpan.FromTicks(Math.Min(
            MaxBackoff.Ticks,
            options.CurrentValue.DeliveryRetryBaseDelay.Ticks << Math.Min(claimed.Delivery.Attempt, 10)));
        await queue.RescheduleAsync(claimed, time.GetUtcNow() + backoff, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mock webhook {DeliveryId} was answered {StatusCode} (attempt {Attempt})")]
    private static partial void LogRejected(ILogger logger, string deliveryId, int statusCode, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mock webhook {DeliveryId} could not be sent (attempt {Attempt})")]
    private static partial void LogUnreachable(ILogger logger, string deliveryId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mock webhook {DeliveryId} was dropped after too many failed attempts")]
    private static partial void LogDropped(ILogger logger, string deliveryId);
}
