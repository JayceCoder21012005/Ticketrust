using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Payments.MoMo;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Payments.MoMo;

/// <summary>Settings of an api that takes payments through MoMo (spec 18), with MoMo's public sandbox keys.</summary>
internal static class MoMoSettings
{
    public static Dictionary<string, string?> For() => new()
    {
        ["Payment:Provider"] = "MoMo",
        ["Payments:MoMo:Endpoint"] = FakeMoMo.Endpoint,
        ["Payments:MoMo:PartnerCode"] = FakeMoMo.PartnerCode,
        ["Payments:MoMo:AccessKey"] = FakeMoMo.AccessKey,
        ["Payments:MoMo:SecretKey"] = FakeMoMo.SecretKey,
        ["Payments:MoMo:IpnUrl"] = "https://tickets.example.com/api/v1/webhooks/payments/momo",
    };
}

/// <summary>
/// Spec 18: MoMo's IPN end to end over HTTP against Postgres. A signed success pays the order and is acknowledged with 204 and
/// no body; the same IPN again issues nothing; a forged one is 401 and changes nothing; the mock's webhooks keep their 200.
/// </summary>
public sealed class MoMoWebhookTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Path = "/api/v1/webhooks/payments/momo";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _event;
    private (Guid Id, long Price) _ga;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: MoMoSettings.For());
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var evt = await db.Events.AsNoTracking().Include(e => e.Tiers).SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        var ga = evt.Tiers.First(t => t.Kind == TicketKind.GeneralAdmission);
        _ga = (ga.Id, ga.Price.Amount);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private sealed record Placed(Guid OrderId, string SessionId, long Total);

    private async Task<Placed> PlaceAsync(string provider = "momo")
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var items = new[] { new OrderItem(_ga.Id, null, new Money(_ga.Price), 2) };
        var total = new Money(_ga.Price * 2);
        var order = Order.Create(Guid.CreateVersion7(), _event, Guid.CreateVersion7(), items, total, new BuyerInfo("Nguyễn Văn An", "an@example.com", null), now.AddMinutes(8), now);
        order.StartPayment(now);
        var session = provider == "momo" ? MoMoPaymentGateway.SessionIdFor($"order:{order.Id:N}:attempt:1") : $"mock_{Guid.NewGuid():N}";
        db.Orders.Add(order);
        db.PaymentAttempts.Add(PaymentAttempt.Create(order.Id, provider, session, $"{FakeMoMo.Endpoint}/v2/gateway/pay?t={session}", total, order.ExpiresAt, now));
        await db.SaveChangesAsync(Ct);
        return new Placed(order.Id, session, total.Amount);
    }

    private Task<HttpResponseMessage> PostAsync(string body, string path = Path, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (signature is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Signature", signature);
        }

        return _client.SendAsync(request, Ct);
    }

    private async Task<(OrderStatus Status, int Tickets, int Webhooks)> StateAsync(Guid orderId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
        return (order.Status, await db.Tickets.CountAsync(t => t.OrderId == orderId, Ct), await db.ProcessedWebhooks.CountAsync(Ct));
    }

    [Fact]
    public async Task Signed_success_IPN_pays_the_order_and_answers_204()
    {
        var placed = await PlaceAsync();

        using var response = await PostAsync(FakeMoMo.SignedIpn(placed.SessionId, placed.Total, 4_100_000_001, 0, DateTimeOffset.UtcNow));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync(Ct));
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(2);
    }

    [Fact]
    public async Task Same_IPN_twice_is_a_duplicate_and_issues_no_second_ticket()
    {
        var placed = await PlaceAsync();
        var body = FakeMoMo.SignedIpn(placed.SessionId, placed.Total, 4_100_000_002, 0, DateTimeOffset.UtcNow);

        using var first = await PostAsync(body);
        var after = await StateAsync(placed.OrderId);
        using var second = await PostAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StateAsync(placed.OrderId)).ShouldBe(after);
    }

    [Fact]
    public async Task A_failed_payment_IPN_marks_the_attempt_failed_and_issues_nothing()
    {
        var placed = await PlaceAsync();

        using var response = await PostAsync(FakeMoMo.SignedIpn(placed.SessionId, placed.Total, 4_100_000_003, 1006, DateTimeOffset.UtcNow));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var state = await StateAsync(placed.OrderId);
        state.Status.ShouldNotBe(OrderStatus.Paid);
        state.Tickets.ShouldBe(0);
    }

    [Fact]
    public async Task Forged_IPN_is_401_and_changes_nothing()
    {
        var placed = await PlaceAsync();
        var before = await StateAsync(placed.OrderId);

        using var tampered = await PostAsync(FakeMoMo.SignedIpn(placed.SessionId, placed.Total, 4_100_000_004, 0, DateTimeOffset.UtcNow, tamper: b => b["amount"] = 1_000));
        using var wrongKey = await PostAsync(FakeMoMo.SignedIpn(placed.SessionId, placed.Total, 4_100_000_005, 0, DateTimeOffset.UtcNow, secretKey: "not-momos-secret-key-0000000000"));

        tampered.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongKey.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StateAsync(placed.OrderId)).ShouldBe(before);
    }

    [Fact]
    public async Task Mock_webhooks_still_answer_200_with_a_receipt()
    {
        var placed = await PlaceAsync(provider: "mock");
        var secret = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MockGatewayOptions>>().Value.WebhookSecret;
        var body = new MockWebhookPayload($"evt_{Guid.NewGuid():N}", MockWebhookPayload.SucceededType, placed.SessionId, placed.Total, "VND", DateTimeOffset.UtcNow).ToJson();

        using var response = await PostAsync(body, "/api/v1/webhooks/payments/mock", WebhookSignature.Sign(secret, body, DateTimeOffset.UtcNow));

        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        JsonDocument.Parse(text).RootElement.GetProperty("outcome").GetString().ShouldBe("paid");
    }
}
