using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TicketRush.Infrastructure.Payments.MoMo;

namespace TicketRush.Api.IntegrationTests.Payments.MoMo;

/// <summary>
/// Plays MoMo's AIO v2 API for spec 18 tests: create (41 on a duplicate orderId), query (1005 for an unknown order) and refund
/// (41 on a duplicate refund orderId; declined refunds are not listed in <c>refundTrans</c>, so a caller cannot rely on that).
/// Every request's signature is checked like MoMo does; a wrong one answers resultCode 11.
/// </summary>
public sealed class FakeMoMo : HttpMessageHandler
{
    public const string PartnerCode = "MOMO";
    public const string AccessKey = "F8BBA842ECF85";
    public const string SecretKey = "K951B6PE1waDMi640xX08PD3vg6EkVlz";
    public const string Endpoint = "https://test-payment.momo.vn";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FakeOrder> _orders = [];
    private readonly HashSet<string> _refundOrderIds = [];
    private long _nextTransId = 4_100_000_000;
    private int _declineRefunds;

    public sealed class FakeOrder(long amount)
    {
        public long Amount { get; } = amount;

        public int ResultCode { get; set; } = 1000;

        public long TransId { get; set; }

        public List<(string OrderId, long Amount, int ResultCode, long TransId)> Refunds { get; } = [];
    }

    /// <summary>Every request body MoMo received, by path.</summary>
    public List<(string Path, JsonObject Body)> Requests { get; } = [];

    public bool Down { get; set; }

    public int CreateCalls => Count("/v2/gateway/api/create");

    public int RefundCalls => Count("/v2/gateway/api/refund");

    public FakeOrder Order(string orderId)
    {
        lock (_gate)
        {
            return _orders[orderId];
        }
    }

    public bool Knows(string orderId)
    {
        lock (_gate)
        {
            return _orders.ContainsKey(orderId);
        }
    }

    /// <summary>The buyer pays on MoMo's page: the order becomes resultCode 0 with a transId. Returns the transId.</summary>
    public long Pay(string orderId, int resultCode = 0)
    {
        lock (_gate)
        {
            var order = _orders[orderId];
            order.ResultCode = resultCode;
            order.TransId = _nextTransId++;
            return order.TransId;
        }
    }

    /// <summary>Puts an order on MoMo's side without going through create (a create whose answer was lost).</summary>
    public void Seed(string orderId, long amount)
    {
        lock (_gate)
        {
            _orders[orderId] = new FakeOrder(amount);
        }
    }

    public void DeclineNextRefund()
    {
        lock (_gate)
        {
            _declineRefunds++;
        }
    }

    /// <summary>An IPN body exactly as MoMo signs it (spec 18 field list).</summary>
    public static string SignedIpn(
        string orderId, long amount, long transId, int resultCode, DateTimeOffset responseTime,
        string extraData = "", string partnerCode = PartnerCode, string secretKey = SecretKey, Action<JsonObject>? tamper = null)
    {
        var fields = new (string Key, string Value)[]
        {
            ("accessKey", AccessKey),
            ("amount", amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("extraData", extraData),
            ("message", resultCode == 0 ? "Thành công." : "Giao dịch thất bại."),
            ("orderId", orderId),
            ("orderInfo", "TicketRush order"),
            ("orderType", "momo_wallet"),
            ("partnerCode", partnerCode),
            ("payType", "napas"),
            ("requestId", Guid.NewGuid().ToString()),
            ("responseTime", responseTime.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("resultCode", resultCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("transId", transId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        var signature = MoMoSignature.Sign(MoMoSignature.Raw(fields), secretKey);
        var body = new JsonObject
        {
            ["partnerCode"] = partnerCode,
            ["orderId"] = orderId,
            ["requestId"] = fields[9].Value,
            ["amount"] = amount,
            ["orderInfo"] = "TicketRush order",
            ["orderType"] = "momo_wallet",
            ["transId"] = transId,
            ["resultCode"] = resultCode,
            ["message"] = fields[3].Value,
            ["payType"] = "napas",
            ["responseTime"] = responseTime.ToUnixTimeMilliseconds(),
            ["extraData"] = extraData,
            ["signature"] = signature,
        };
        tamper?.Invoke(body);
        return body.ToJsonString();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("MoMo is down (test).");
        }

        var path = request.RequestUri!.AbsolutePath;
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        lock (_gate)
        {
            Requests.Add((path, body));
            var answer = path switch
            {
                "/v2/gateway/api/create" => Create(body),
                "/v2/gateway/api/query" => Query(body),
                "/v2/gateway/api/refund" => Refund(body),
                _ => null,
            };
            return answer is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer.ToJsonString(), Encoding.UTF8, "application/json") };
        }
    }

    private int Count(string path)
    {
        lock (_gate)
        {
            return Requests.Count(r => r.Path == path);
        }
    }

    private static string S(JsonObject body, string name) => body[name]?.ToString() ?? string.Empty;

    private static bool Signed(JsonObject body, params string[] fields) =>
        S(body, "partnerCode") == PartnerCode
        && MoMoSignature.Matches(
            MoMoSignature.Raw([.. fields.Select(f => (f, f == "accessKey" ? AccessKey : S(body, f)))]),
            SecretKey,
            S(body, "signature"));

    private static JsonObject Result(JsonObject body, int code, string message) => new()
    {
        ["partnerCode"] = PartnerCode,
        ["orderId"] = S(body, "orderId"),
        ["requestId"] = S(body, "requestId"),
        ["responseTime"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ["resultCode"] = code,
        ["message"] = message,
    };

    private JsonObject Create(JsonObject body)
    {
        if (!Signed(body, "accessKey", "amount", "extraData", "ipnUrl", "orderId", "orderInfo", "partnerCode", "redirectUrl", "requestId", "requestType"))
        {
            return Result(body, 11, "Signature mismatch.");
        }

        var orderId = S(body, "orderId");
        if (_orders.ContainsKey(orderId))
        {
            return Result(body, 41, "Request rejected due to duplicated orderId.");
        }

        var amount = body["amount"]!.GetValue<long>();
        _orders[orderId] = new FakeOrder(amount);
        var result = Result(body, 0, "Thành công.");
        result["amount"] = amount;
        result["payUrl"] = $"{Endpoint}/v2/gateway/pay?t={orderId}";
        return result;
    }

    private JsonObject Query(JsonObject body)
    {
        if (!Signed(body, "accessKey", "orderId", "partnerCode", "requestId"))
        {
            return Result(body, 11, "Signature mismatch.");
        }

        if (!_orders.TryGetValue(S(body, "orderId"), out var order))
        {
            return Result(body, 1005, "Transaction expired or did not exist.");
        }

        var result = Result(body, order.ResultCode, "ok");
        result["amount"] = order.Amount;
        result["transId"] = order.TransId;
        result["payType"] = order.ResultCode == 0 ? "napas" : "";
        result["refundTrans"] = new JsonArray([.. order.Refunds.Select(r => (JsonNode)new JsonObject
        {
            ["orderId"] = r.OrderId,
            ["amount"] = r.Amount,
            ["resultCode"] = r.ResultCode,
            ["transId"] = r.TransId,
        })]);
        return result;
    }

    private JsonObject Refund(JsonObject body)
    {
        if (!Signed(body, "accessKey", "amount", "description", "orderId", "partnerCode", "requestId", "transId"))
        {
            return Result(body, 11, "Signature mismatch.");
        }

        var refundOrderId = S(body, "orderId");
        if (!_refundOrderIds.Add(refundOrderId) || _orders.ContainsKey(refundOrderId))
        {
            return Result(body, 41, "Request rejected due to duplicated orderId.");
        }

        var transId = body["transId"]!.GetValue<long>();
        var payment = _orders.Values.SingleOrDefault(o => o.TransId == transId && o.TransId != 0);
        if (payment is null || payment.ResultCode != 0)
        {
            return Result(body, 1080, "Original transaction not found.");
        }

        if (_declineRefunds > 0)
        {
            _declineRefunds--;
            return Result(body, 1002, "Refund declined (test).");
        }

        var amount = body["amount"]!.GetValue<long>();
        var refundTransId = _nextTransId++;
        payment.Refunds.Add((refundOrderId, amount, 0, refundTransId));
        var result = Result(body, 0, "Thành công.");
        result["amount"] = amount;
        result["transId"] = refundTransId;
        return result;
    }
}
