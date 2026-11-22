using Microsoft.AspNetCore.Mvc;

namespace TicketRush.Api.Features.Webhooks;

/// <param name="Outcome">
/// What the event led to: <c>paid</c>, <c>refund_pending</c>, <c>failed</c>, <c>duplicate</c>, <c>ignored</c> or
/// <c>unknown_session</c>.
/// </param>
public sealed record PaymentWebhookReceipt(string Outcome);

/// <summary>
/// <c>POST /webhooks/payments/{provider}</c> (spec 08): the payment provider's server-to-server notification. Anonymous,
/// authenticated by its signature only; no idempotency filter (the dedupe is <c>processed_webhooks</c>) and no user rate
/// limit. 200 (204 with no body for MoMo, spec 18) after every committed decision, 5xx only when nothing was committed (so the provider retries).
/// </summary>
public static class PaymentWebhookEndpoints
{
    /// <summary>Largest body accepted (spec 08); anything bigger is 413 before it is read in full.</summary>
    public const int MaxBodyBytes = 64 * 1024;

    public static RouteGroupBuilder MapPaymentWebhookEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/webhooks/payments/{provider}", ReceiveAsync)
            .WithTags("Webhooks")
            .WithName("ReceivePaymentWebhook")
            .AllowAnonymous()
            .DisableRateLimiting()
            .WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes))
            .Produces<PaymentWebhookReceipt>()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return v1;
    }

    private static Task<IResult> ReceiveAsync(string provider, HttpRequest request, PaymentWebhookReceiver receiver, CancellationToken ct) =>
        receiver.ReceiveAsync(provider, request, ct);
}
