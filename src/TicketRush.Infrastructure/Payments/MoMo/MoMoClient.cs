using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>What <see cref="MoMoClient.CreateAsync"/> sends besides the partner fields and the signature.</summary>
public sealed record MoMoCreateRequest(
    string OrderId,
    long Amount,
    string OrderInfo,
    string RedirectUrl,
    string IpnUrl,
    string ExtraData,
    int OrderExpireTimeMinutes);

public sealed record MoMoCreateResponse(int ResultCode, string? Message, string? PayUrl);

public sealed record MoMoRefundTransaction(string OrderId, long Amount, int ResultCode, long TransId);

public sealed record MoMoQueryResponse(int ResultCode, string? Message, long TransId, long Amount, IReadOnlyList<MoMoRefundTransaction>? RefundTrans);

public sealed record MoMoRefundResponse(int ResultCode, string? Message, long TransId);

/// <summary>
/// MoMo AIO v2 over HTTP (spec 18): <c>/v2/gateway/api/create</c>, <c>/query</c> and <c>/refund</c>, each signed with
/// <see cref="MoMoSignature"/> and a fresh <c>requestId</c>. A non-2xx answer or an unreadable body throws
/// <see cref="HttpRequestException"/> (transient: the resilience decorator retries it); a 2xx answer is returned as is, its
/// <c>resultCode</c> is the caller's to judge. One named client per call, so DNS changes are picked up.
/// </summary>
public sealed class MoMoClient(IHttpClientFactory clients, IOptionsMonitor<MoMoOptions> options)
{
    public const string HttpClientName = "momo";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Task<MoMoCreateResponse> CreateAsync(MoMoCreateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var o = options.CurrentValue;
        const string requestType = "payWithMethod";
        var requestId = Guid.NewGuid().ToString();
        var signature = Sign(
            o,
            ("accessKey", o.AccessKey),
            ("amount", Invariant(request.Amount)),
            ("extraData", request.ExtraData),
            ("ipnUrl", request.IpnUrl),
            ("orderId", request.OrderId),
            ("orderInfo", request.OrderInfo),
            ("partnerCode", o.PartnerCode),
            ("redirectUrl", request.RedirectUrl),
            ("requestId", requestId),
            ("requestType", requestType));
        return PostAsync<MoMoCreateResponse>("/v2/gateway/api/create", new
        {
            partnerCode = o.PartnerCode,
            accessKey = o.AccessKey,
            requestId,
            amount = request.Amount,
            orderId = request.OrderId,
            orderInfo = request.OrderInfo,
            redirectUrl = request.RedirectUrl,
            ipnUrl = request.IpnUrl,
            extraData = request.ExtraData,
            requestType,
            autoCapture = true,
            orderExpireTime = request.OrderExpireTimeMinutes,
            lang = "vi",
            signature,
        }, ct);
    }

    public Task<MoMoQueryResponse> QueryAsync(string orderId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        var o = options.CurrentValue;
        var requestId = Guid.NewGuid().ToString();
        var signature = Sign(o, ("accessKey", o.AccessKey), ("orderId", orderId), ("partnerCode", o.PartnerCode), ("requestId", requestId));
        return PostAsync<MoMoQueryResponse>("/v2/gateway/api/query", new
        {
            partnerCode = o.PartnerCode,
            accessKey = o.AccessKey,
            requestId,
            orderId,
            lang = "vi",
            signature,
        }, ct);
    }

    public Task<MoMoRefundResponse> RefundAsync(string orderId, long amount, long transId, string description, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderId);
        var o = options.CurrentValue;
        var requestId = Guid.NewGuid().ToString();
        var signature = Sign(
            o,
            ("accessKey", o.AccessKey),
            ("amount", Invariant(amount)),
            ("description", description),
            ("orderId", orderId),
            ("partnerCode", o.PartnerCode),
            ("requestId", requestId),
            ("transId", Invariant(transId)));
        return PostAsync<MoMoRefundResponse>("/v2/gateway/api/refund", new
        {
            partnerCode = o.PartnerCode,
            accessKey = o.AccessKey,
            requestId,
            orderId,
            amount,
            transId,
            lang = "vi",
            description,
            signature,
        }, ct);
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Sign(MoMoOptions o, params (string Key, string Value)[] fields) => MoMoSignature.Sign(MoMoSignature.Raw(fields), o.SecretKey);

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        var client = clients.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(path, body, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"MoMo answered {(int)response.StatusCode} on {path}.", null, response.StatusCode);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new HttpRequestException($"MoMo sent an empty body on {path}.");
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException($"MoMo sent an unreadable body on {path}.", ex);
        }
    }
}
