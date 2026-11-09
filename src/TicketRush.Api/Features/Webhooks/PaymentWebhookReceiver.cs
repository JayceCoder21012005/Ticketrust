using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Orders;

namespace TicketRush.Api.Features.Webhooks;

/// <summary>
/// HTTP side of a payment webhook: resolve the provider (unknown → 404), read at most 64 KB (more → 413), decode the bytes
/// as strict UTF-8 exactly as signed (no BOM stripping, no replacement characters: anything else fails closed as 401),
/// let the gateway verify and parse, then hand the event to <see cref="FulfillOrderHandler"/>. A bad signature, stale
/// timestamp or malformed header is 401 and touches nothing; a blank secret is a server fault (500, fail closed). Logs never
/// carry the body or the secret.
/// </summary>
public sealed partial class PaymentWebhookReceiver(
    IPaymentGatewayResolver gateways,
    FulfillOrderHandler handler,
    PaymentWebhookMetrics metrics,
    ILogger<PaymentWebhookReceiver> logger)
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public async Task<IResult> ReceiveAsync(string provider, HttpRequest request, CancellationToken ct)
    {
        if (gateways.Get(provider) is not { } gateway)
        {
            metrics.Record("unknown", "unknown_provider");
            return Problem(StatusCodes.Status404NotFound, "webhook.unknown_provider", "No payment provider has this name.");
        }

        if (await ReadBoundedAsync(request, ct) is not { } body)
        {
            metrics.Record(gateway.Provider, "too_large");
            return Problem(StatusCodes.Status413PayloadTooLarge, "webhook.too_large", $"Webhook bodies are limited to {PaymentWebhookEndpoints.MaxBodyBytes} bytes.");
        }

        var refusal = TryVerify(gateway, request, body, out var webhook);
        return refusal is not null ? refusal : await DecideAsync(gateway.Provider, webhook, ct);
    }

    /// <returns>Null when the body could be read in full within the limit; the bytes otherwise.</returns>
    private static async Task<byte[]?> ReadBoundedAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > PaymentWebhookEndpoints.MaxBodyBytes)
        {
            return null;
        }

        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = PaymentWebhookEndpoints.MaxBodyBytes;
        }

        // One byte more than allowed tells "exactly the limit" from "over it" without reading the rest.
        var buffer = new byte[PaymentWebhookEndpoints.MaxBodyBytes + 1];
        var length = 0;
        try
        {
            int read;
            while (length < buffer.Length && (read = await request.Body.ReadAsync(buffer.AsMemory(length), ct)) > 0)
            {
                length += read;
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return length > PaymentWebhookEndpoints.MaxBodyBytes ? null : buffer[..length];
    }

    /// <returns>The refusal, or null when <paramref name="webhook"/> is authentic and parsed.</returns>
    private ProblemHttpResult? TryVerify(IPaymentGateway gateway, HttpRequest request, byte[] body, out WebhookEvent webhook)
    {
        webhook = null!;
        try
        {
            webhook = gateway.VerifyAndParse(request.ToWebhookHeaders(), StrictUtf8.GetString(body));
            return webhook.ProviderEventId.Length > ProcessedWebhook.MaxProviderEventIdLength
                ? InvalidPayload(gateway.Provider)
                : null;
        }
        catch (DecoderFallbackException)
        {
            // Not the bytes of any string a provider could have signed. (Derives from ArgumentException: caught first.)
            return InvalidSignature(gateway.Provider, "The body is not valid UTF-8.");
        }
        catch (InvalidWebhookSignatureException ex)
        {
            return InvalidSignature(gateway.Provider, ex.Message);
        }
        catch (InvalidWebhookPayloadException)
        {
            return InvalidPayload(gateway.Provider);
        }
        catch (ArgumentException ex)
        {
            return Misconfigured(gateway.Provider, ex);
        }
    }

    /// <summary>A blank webhook secret: never accept, never blame the sender (fail closed). Nothing was read from the database yet.</summary>
    private ProblemHttpResult Misconfigured(string provider, ArgumentException exception)
    {
        LogMisconfigured(provider, exception);
        metrics.Record(provider, "misconfigured");
        return Problem(StatusCodes.Status500InternalServerError, "webhook.misconfigured", "Webhook verification is not configured.");
    }

    private async Task<IResult> DecideAsync(string provider, WebhookEvent webhook, CancellationToken ct)
    {
        PaymentWebhookOutcome outcome;
        try
        {
            outcome = await handler.HandleAsync(provider, webhook, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nothing was committed (the transaction rolled back): a 5xx makes the provider deliver the event again.
            LogNotProcessed(provider, webhook.ProviderEventId, ex);
            metrics.Record(provider, "error");
            return Problem(StatusCodes.Status500InternalServerError, "webhook.not_processed", "The webhook could not be processed; please deliver it again.");
        }

        var name = NameOf(outcome);
        metrics.Record(provider, name);
        LogDecided(provider, webhook.ProviderEventId, name);
        return TypedResults.Ok(new PaymentWebhookReceipt(name));
    }

    private static string NameOf(PaymentWebhookOutcome outcome) => outcome switch
    {
        PaymentWebhookOutcome.Paid => "paid",
        PaymentWebhookOutcome.RefundPending => "refund_pending",
        PaymentWebhookOutcome.Failed => "failed",
        PaymentWebhookOutcome.Duplicate => "duplicate",
        PaymentWebhookOutcome.UnknownSession => "unknown_session",
        PaymentWebhookOutcome.AmountMismatchAfterStatus => "amount_mismatch_after_status",
        _ => "ignored",
    };

    private ProblemHttpResult InvalidSignature(string provider, string reason)
    {
        LogInvalidSignature(provider, reason);
        metrics.Record(provider, "invalid_signature");
        return Problem(StatusCodes.Status401Unauthorized, "webhook.invalid_signature", "The webhook signature is not valid.");
    }

    private ProblemHttpResult InvalidPayload(string provider)
    {
        LogInvalidPayload(provider);
        metrics.Record(provider, "invalid_payload");
        return Problem(StatusCodes.Status400BadRequest, "webhook.invalid_payload", "The webhook body is not an event this provider sends.");
    }

    private static ProblemHttpResult Problem(int status, string code, string title) =>
        TypedResults.Problem(statusCode: status, title: title, type: $"https://httpstatuses.io/{status}", extensions: new Dictionary<string, object?> { ["code"] = code });

    [LoggerMessage(EventId = 8110, Level = LogLevel.Information, Message = "Payment webhook {ProviderEventId} from {Provider}: {Outcome}.")]
    private partial void LogDecided(string provider, string providerEventId, string outcome);

    [LoggerMessage(EventId = 8111, Level = LogLevel.Warning, Message = "Rejected a payment webhook for {Provider}: {Reason}")]
    private partial void LogInvalidSignature(string provider, string reason);

    [LoggerMessage(EventId = 8112, Level = LogLevel.Warning, Message = "Rejected an authentic payment webhook for {Provider}: the body is not a known event.")]
    private partial void LogInvalidPayload(string provider);

    [LoggerMessage(EventId = 8113, Level = LogLevel.Critical, Message = "Payment webhooks for {Provider} cannot be verified: the webhook secret is not configured.")]
    private partial void LogMisconfigured(string provider, Exception exception);

    [LoggerMessage(EventId = 8114, Level = LogLevel.Error, Message = "Payment webhook {ProviderEventId} from {Provider} was not processed; nothing was committed and the provider will retry.")]
    private partial void LogNotProcessed(string provider, string providerEventId, Exception exception);
}

/// <summary>Counter <c>ticketrush.payments.webhooks</c>, tagged with <c>provider</c> and <c>outcome</c>.</summary>
public sealed class PaymentWebhookMetrics
{
    public const string MeterName = "TicketRush.Payments";

    private readonly Counter<long> _webhooks;

    public PaymentWebhookMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        _webhooks = meterFactory.Create(MeterName)
            .CreateCounter<long>("ticketrush.payments.webhooks", description: "Payment webhooks received, by outcome.");
    }

    public void Record(string provider, string outcome) =>
        _webhooks.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("outcome", outcome));
}
