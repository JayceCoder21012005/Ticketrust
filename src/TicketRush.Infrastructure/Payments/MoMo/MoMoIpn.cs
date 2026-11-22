using System.Globalization;
using System.Text.Json;
using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>
/// The body MoMo POSTs to the IPN URL (spec 18). Values are kept as sent (text) so the signature string is rebuilt from exactly
/// what was signed; numbers are read as JSON numbers or numeric strings.
/// </summary>
internal sealed record MoMoIpn(
    string PartnerCode,
    string OrderId,
    string RequestId,
    long Amount,
    string OrderInfo,
    string OrderType,
    long TransId,
    int ResultCode,
    string Message,
    string PayType,
    long ResponseTime,
    string ExtraData,
    string? Signature)
{
    public static MoMoIpn Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidWebhookPayloadException("A MoMo IPN is a JSON object.");
            }

            return new MoMoIpn(
                Text(root, "partnerCode", required: true),
                Text(root, "orderId", required: true),
                Text(root, "requestId"),
                Number(root, "amount"),
                Text(root, "orderInfo"),
                Text(root, "orderType"),
                Number(root, "transId"),
                (int)Number(root, "resultCode"),
                Text(root, "message"),
                Text(root, "payType"),
                Number(root, "responseTime"),
                Text(root, "extraData"),
                root.TryGetProperty("signature", out var signature) && signature.ValueKind == JsonValueKind.String ? signature.GetString() : null);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
        {
            throw new InvalidWebhookPayloadException("The MoMo IPN body is not valid.", ex);
        }
    }

    public string RawSignature(string accessKey) => MoMoSignature.Raw(
        ("accessKey", accessKey),
        ("amount", Invariant(Amount)),
        ("extraData", ExtraData),
        ("message", Message),
        ("orderId", OrderId),
        ("orderInfo", OrderInfo),
        ("orderType", OrderType),
        ("partnerCode", PartnerCode),
        ("payType", PayType),
        ("requestId", RequestId),
        ("responseTime", Invariant(ResponseTime)),
        ("resultCode", Invariant(ResultCode)),
        ("transId", Invariant(TransId)));

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(JsonElement root, string name, bool required = false)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return required ? throw new InvalidWebhookPayloadException($"The MoMo IPN has no {name}.") : string.Empty;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static long Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new InvalidWebhookPayloadException($"The MoMo IPN has no {name}.");
        }

        return value.ValueKind == JsonValueKind.String
            ? long.Parse(value.GetString()!, NumberStyles.Integer, CultureInfo.InvariantCulture)
            : value.GetInt64();
    }
}
