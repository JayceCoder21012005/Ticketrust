using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>
/// MoMo AIO v2 (spec 18), sandbox-first. One MoMo order per idempotency key: its orderId (our session id) is derived from the
/// key, and the first answer is cached (<see cref="MoMoSessionCache"/>) because MoMo refuses a repeated orderId (41). The status
/// and refunds come from <c>/query</c> and <c>/refund</c>; IPNs are verified with the IPN field list. Wrapped by
/// <see cref="ResilientPaymentGateway"/> like the mock.
/// </summary>
public sealed class MoMoPaymentGateway(
    MoMoClient client,
    MoMoSessionCache sessions,
    IOptionsMonitor<MoMoOptions> options,
    TimeProvider time) : IPaymentGateway
{
    public const string ProviderName = "momo";

    internal const int Succeeded = 0;
    internal const int Authorized = 9000;
    internal const int DuplicateOrderId = 41;
    internal const int ExpiredOrUnknown = 1005;

    /// <summary>Codes MoMo uses while the buyer has not finished (1000) or the payment is being processed (7000, 7002).</summary>
    internal static readonly int[] PendingCodes = [1000, 7000, 7002];

    /// <summary>Codes that mean MoMo itself is in trouble (maintenance, unknown error): transient, so the decorator retries.</summary>
    private static readonly int[] TransientCodes = [10, 99];

    /// <summary>Most attempts at a fresh refund orderId within one call (each 41 moves to the next suffix).</summary>
    private const int RefundOrderIdTries = 5;

    public string Provider => ProviderName;

    /// <summary>MoMo orderId (our session id) for an idempotency key: <c>TR</c> + 40 hex of its SHA-256 (MoMo's charset has no <c>:</c>).</summary>
    public static string SessionIdFor(string idempotencyKey) => "TR" + HexOf(idempotencyKey)[..40];

    /// <summary>Prefix of the MoMo orderIds of the refunds made under one idempotency key; each try appends <c>-{n}</c>.</summary>
    public static string RefundOrderIdPrefix(string idempotencyKey) => "RF" + HexOf(idempotencyKey)[..38];

    public async Task<PaymentSession> CreateSessionAsync(Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(returnUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (await sessions.GetAsync(idempotencyKey, ct) is { } cached)
        {
            return cached;
        }

        var o = options.CurrentValue;
        RequireAmount(order.Total, o);
        var now = time.GetUtcNow();
        var minutes = (int)Math.Floor((expiresAt - now).TotalMinutes);
        if (minutes < 1)
        {
            throw new PaymentRejectedException("The payment window is shorter than MoMo's one-minute minimum.");
        }

        var sessionId = SessionIdFor(idempotencyKey);
        var extraData = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { orderId = order.Id })));
        var response = await client.CreateAsync(
            new MoMoCreateRequest(sessionId, order.Total.Amount, $"TicketRush order {order.Id.ToString("N")[..10]}", returnUrl.ToString(), o.IpnUrl, extraData, minutes),
            ct);
        if (response.ResultCode == DuplicateOrderId)
        {
            // Another call with this key won the race and cached its session, or our earlier answer was lost (then the buyer's
            // next try gets a new attempt number, hence a new key).
            return await sessions.GetAsync(idempotencyKey, ct)
                ?? throw new PaymentRejectedException("MoMo already holds an order for this attempt and its payment page is unknown.");
        }

        ThrowIfTransient(response.ResultCode, "create");
        if (response.ResultCode != Succeeded || !Uri.TryCreate(response.PayUrl, UriKind.Absolute, out var payUrl))
        {
            throw new PaymentRejectedException($"MoMo refused the payment (resultCode {response.ResultCode}).");
        }

        var session = new PaymentSession(sessionId, payUrl, now.AddMinutes(minutes));
        await sessions.SetAsync(idempotencyKey, session, session.ExpiresAt - now + o.SessionMargin, ct);
        return session;
    }

    public async Task<PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var response = await client.QueryAsync(sessionId, ct);
        ThrowIfTransient(response.ResultCode, "query");
        return StatusOf(response.ResultCode);
    }

    public async Task<RefundResult> RefundAsync(string sessionId, Money amount, string idempotencyKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (amount.Amount <= 0 || amount.Currency != Money.DefaultCurrency)
        {
            return new RefundResult(string.Empty, RefundStatus.Failed, "invalid_amount");
        }

        var payment = await client.QueryAsync(sessionId, ct);
        ThrowIfTransient(payment.ResultCode, "query");
        if (StatusOf(payment.ResultCode) != PaymentStatus.Succeeded || payment.TransId == 0)
        {
            return new RefundResult(string.Empty, RefundStatus.Failed, "payment_not_succeeded");
        }

        var prefix = RefundOrderIdPrefix(idempotencyKey) + "-";
        var earlier = (payment.RefundTrans ?? []).Where(r => r.OrderId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (earlier.FirstOrDefault(r => r.ResultCode == Succeeded) is { } done)
        {
            // This key's refund already went through: replay it, never pay twice.
            return new RefundResult(done.OrderId, RefundStatus.Succeeded);
        }

        // MoMo refuses a repeated refund orderId, and need not list declined tries: start past the ones it lists, move on at 41.
        for (int n = earlier.Count, tries = 0; tries < RefundOrderIdTries; n++, tries++)
        {
            var refundOrderId = prefix + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var response = await client.RefundAsync(refundOrderId, amount.Amount, payment.TransId, $"TicketRush refund {sessionId}", ct);
            if (response.ResultCode == DuplicateOrderId)
            {
                continue;
            }

            ThrowIfTransient(response.ResultCode, "refund");
            return response.ResultCode == Succeeded
                ? new RefundResult(refundOrderId, RefundStatus.Succeeded)
                : new RefundResult(refundOrderId, RefundStatus.Failed, $"momo_{response.ResultCode}");
        }

        return new RefundResult(string.Empty, RefundStatus.Failed, "momo_duplicate_refund_order");
    }

    public bool AcknowledgesWithNoContent => true;

    /// <summary>
    /// MoMo's IPN (spec 18): the signature sits in the JSON body and covers the IPN field list, with our accessKey (not sent).
    /// <c>responseTime</c> must be within <see cref="WebhookTolerance.Window"/>. The event id is <c>{transId}:{resultCode}</c>; a
    /// pending code is no decision (invalid payload), a final non-success code is a failed payment.
    /// </summary>
    public WebhookEvent VerifyAndParse(WebhookHeaders headers, string body)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(body);
        var o = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(o.SecretKey) || string.IsNullOrWhiteSpace(o.PartnerCode))
        {
            // Not a misconfiguration: MoMo is registered on every host but only configured where it is the provider (the options
            // validator demands the keys there). Without keys nothing can be authentic, and nobody may turn that into 500s.
            throw new InvalidWebhookSignatureException("MoMo is not configured on this host.");
        }

        var ipn = MoMoIpn.Parse(body);
        if (!string.Equals(ipn.PartnerCode, o.PartnerCode, StringComparison.Ordinal))
        {
            throw new InvalidWebhookPayloadException("The IPN is for another MoMo partner.");
        }

        if (!MoMoSignature.Matches(ipn.RawSignature(o.AccessKey), o.SecretKey, ipn.Signature))
        {
            throw new InvalidWebhookSignatureException("The MoMo IPN signature does not match.");
        }

        var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(ipn.ResponseTime);
        if ((time.GetUtcNow() - occurredAt).Duration() > WebhookTolerance.Window)
        {
            throw new InvalidWebhookSignatureException("The MoMo IPN responseTime is outside the accepted window.");
        }

        var type = StatusOf(ipn.ResultCode) switch
        {
            PaymentStatus.Succeeded => WebhookEventType.Succeeded,
            PaymentStatus.Pending => throw new InvalidWebhookPayloadException($"MoMo resultCode {ipn.ResultCode} is not a final outcome."),
            _ => WebhookEventType.Failed,
        };
        return new WebhookEvent(
            $"{ipn.TransId.ToString(System.Globalization.CultureInfo.InvariantCulture)}:{ipn.ResultCode.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            ipn.OrderId,
            type,
            new Money(ipn.Amount),
            occurredAt);
    }

    internal static PaymentStatus StatusOf(int resultCode) => resultCode switch
    {
        Succeeded or Authorized => PaymentStatus.Succeeded,
        ExpiredOrUnknown => PaymentStatus.Expired,
        _ when PendingCodes.Contains(resultCode) => PaymentStatus.Pending,
        _ => PaymentStatus.Failed,
    };

    private static void RequireAmount(Money total, MoMoOptions o)
    {
        if (total.Currency != Money.DefaultCurrency)
        {
            throw new PaymentRejectedException($"MoMo only takes VND, not {total.Currency}.");
        }

        if (total.Amount < o.MinAmount || total.Amount > o.MaxAmount)
        {
            throw new PaymentRejectedException($"MoMo takes payments of {o.MinAmount}–{o.MaxAmount} VND.");
        }
    }

    private static void ThrowIfTransient(int resultCode, string call)
    {
        if (TransientCodes.Contains(resultCode))
        {
            throw new HttpRequestException($"MoMo {call} answered resultCode {resultCode} (temporary).");
        }
    }

    private static string HexOf(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
