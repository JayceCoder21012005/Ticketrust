using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Domain.Orders;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Payments.MoMo;
using TicketRush.Infrastructure.Redis;
using Order = TicketRush.Domain.Orders.Order;

namespace TicketRush.Api.IntegrationTests.Payments.MoMo;

/// <summary>Spec 18: the MoMo adapter against a fake MoMo (signatures checked) and a real Redis.</summary>
public sealed class MoMoPaymentGatewayTests(ContainersFixture containers) : IAsyncLifetime
{
    private static readonly Uri ReturnUrl = new("https://tickets.example.com/checkout/abc/result");

    private ConnectionMultiplexer _redis = null!;
    private ServiceProvider _services = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeMoMo Fake { get; } = new();

    private FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 22, 10, 0, 0, TimeSpan.Zero));

    private MoMoOptions Options { get; } = new()
    {
        Endpoint = FakeMoMo.Endpoint,
        PartnerCode = FakeMoMo.PartnerCode,
        AccessKey = FakeMoMo.AccessKey,
        SecretKey = FakeMoMo.SecretKey,
        IpnUrl = "https://tickets.example.com/api/v1/webhooks/payments/momo",
    };

    private MoMoPaymentGateway Gateway { get; set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var services = new ServiceCollection();
        services.AddHttpClient(MoMoClient.HttpClientName, c => c.BaseAddress = new Uri(FakeMoMo.Endpoint))
            .ConfigurePrimaryHttpMessageHandler(() => Fake);
        _services = services.BuildServiceProvider();
        var monitor = new StaticOptionsMonitor<MoMoOptions>(Options);
        var keys = new RedisKeys(Microsoft.Extensions.Options.Options.Create(new RedisOptions { Environment = $"m{Guid.NewGuid():N}"[..12] }));
        Gateway = new MoMoPaymentGateway(
            new MoMoClient(_services.GetRequiredService<IHttpClientFactory>(), monitor),
            new MoMoSessionCache(_redis, keys),
            monitor,
            Time);
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _redis.DisposeAsync();
    }

    private Order NewOrder(long total = 1_000_000, string currency = "VND") =>
        Order.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            [new OrderItem(Guid.NewGuid(), null, new Money(total, currency), 1)],
            new Money(total, currency),
            new BuyerInfo("Nguyen Van A", "a@example.com", null),
            Time.GetUtcNow().AddMinutes(8),
            Time.GetUtcNow());

    private Task<PaymentSession> CreateAsync(Order? order = null, string key = "order:x:attempt:1", TimeSpan? lifetime = null) =>
        Gateway.CreateSessionAsync(order ?? NewOrder(), ReturnUrl, Time.GetUtcNow() + (lifetime ?? TimeSpan.FromMinutes(7)), key, Ct);

    [Fact]
    public async Task CreateSession_returns_MoMo_payUrl_and_session_id_derived_from_the_key()
    {
        var order = NewOrder(total: 1_200_000);

        var session = await CreateAsync(order, "order:abc:attempt:1", TimeSpan.FromMinutes(7.5));

        session.SessionId.ShouldBe(MoMoPaymentGateway.SessionIdFor("order:abc:attempt:1"));
        session.SessionId.Length.ShouldBe(42);
        session.SessionId.ShouldStartWith("TR");
        session.RedirectUrl.ToString().ShouldBe($"{FakeMoMo.Endpoint}/v2/gateway/pay?t={session.SessionId}");
        session.ExpiresAt.ShouldBe(Time.GetUtcNow().AddMinutes(7));
        Gateway.Provider.ShouldBe("momo");

        var sent = Fake.Requests.Single(r => r.Path == "/v2/gateway/api/create").Body;
        sent["requestType"]!.ToString().ShouldBe("payWithMethod");
        sent["amount"]!.GetValue<long>().ShouldBe(1_200_000);
        sent["orderExpireTime"]!.GetValue<int>().ShouldBe(7);
        sent["ipnUrl"]!.ToString().ShouldBe(Options.IpnUrl);
        sent["redirectUrl"]!.ToString().ShouldBe(ReturnUrl.ToString());
        sent["autoCapture"]!.GetValue<bool>().ShouldBeTrue();
        sent["lang"]!.ToString().ShouldBe("vi");
        var extra = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sent["extraData"]!.ToString()));
        extra.ShouldContain(order.Id.ToString());
    }

    [Fact]
    public async Task CreateSession_twice_with_the_same_key_creates_one_MoMo_order()
    {
        var order = NewOrder();

        var first = await CreateAsync(order, "order:dbl:attempt:1");
        var second = await CreateAsync(order, "order:dbl:attempt:1");
        var other = await CreateAsync(order, "order:dbl:attempt:2");

        Fake.CreateCalls.ShouldBe(2);
        second.ShouldBe(first);
        other.SessionId.ShouldNotBe(first.SessionId);
    }

    [Fact]
    public async Task CreateSession_refuses_non_VND_and_out_of_range_amounts()
    {
        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(NewOrder(total: 100, currency: "USD")));
        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(NewOrder(total: 999)));
        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(NewOrder(total: 50_000_001)));
        Fake.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateSession_refuses_an_already_expired_window()
    {
        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(lifetime: TimeSpan.Zero));
        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(lifetime: TimeSpan.FromSeconds(-5)));
        Fake.CreateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task CreateSession_duplicate_orderId_without_cache_is_rejected()
    {
        Fake.Seed(MoMoPaymentGateway.SessionIdFor("order:lost:attempt:1"), 1_000_000);

        await Should.ThrowAsync<PaymentRejectedException>(() => CreateAsync(key: "order:lost:attempt:1"));
    }

    [Fact]
    public async Task GetStatus_maps_result_codes()
    {
        var session = await CreateAsync();
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);

        Fake.Pay(session.SessionId);
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Succeeded);

        Fake.Order(session.SessionId).ResultCode = 9000;
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Succeeded);

        Fake.Order(session.SessionId).ResultCode = 7002;
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);

        Fake.Order(session.SessionId).ResultCode = 1006;
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Failed);

        Fake.Order(session.SessionId).ResultCode = 1005;
        (await Gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Expired);

        (await Gateway.GetStatusAsync("TRunknown", Ct)).ShouldBe(PaymentStatus.Expired);
    }

    [Fact]
    public async Task Refund_pays_back_through_refund_with_transId_and_replays_a_success()
    {
        var session = await CreateAsync(NewOrder(total: 1_000_000));
        var transId = Fake.Pay(session.SessionId);

        var first = await Gateway.RefundAsync(session.SessionId, new Money(400_000), "refund:order:1", Ct);
        var again = await Gateway.RefundAsync(session.SessionId, new Money(400_000), "refund:order:1", Ct);

        first.IsSuccess.ShouldBeTrue();
        again.ShouldBe(first);
        var refunds = Fake.Order(session.SessionId).Refunds;
        refunds.Count.ShouldBe(1);
        refunds[0].Amount.ShouldBe(400_000);
        refunds[0].OrderId.ShouldBe(MoMoPaymentGateway.RefundOrderIdPrefix("refund:order:1") + "-0");
        first.RefundId.ShouldBe(refunds[0].OrderId);
        Fake.Requests.Single(r => r.Path == "/v2/gateway/api/refund").Body["transId"]!.GetValue<long>().ShouldBe(transId);
    }

    [Fact]
    public async Task Refund_after_a_decline_uses_a_new_orderId()
    {
        var session = await CreateAsync();
        Fake.Pay(session.SessionId);
        Fake.DeclineNextRefund();

        var declined = await Gateway.RefundAsync(session.SessionId, new Money(1_000_000), "refund:order:2", Ct);
        var retried = await Gateway.RefundAsync(session.SessionId, new Money(1_000_000), "refund:order:2", Ct);

        declined.Status.ShouldBe(RefundStatus.Failed);
        declined.FailureReason.ShouldBe("momo_1002");
        retried.IsSuccess.ShouldBeTrue();
        retried.RefundId.ShouldBe(MoMoPaymentGateway.RefundOrderIdPrefix("refund:order:2") + "-1");
        Fake.Order(session.SessionId).Refunds.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Refund_of_an_unpaid_payment_fails_with_payment_not_succeeded()
    {
        var session = await CreateAsync();

        var result = await Gateway.RefundAsync(session.SessionId, new Money(1_000_000), "refund:order:3", Ct);

        result.Status.ShouldBe(RefundStatus.Failed);
        result.FailureReason.ShouldBe("payment_not_succeeded");
        Fake.RefundCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Gateway_down_throws_HttpRequestException()
    {
        var session = await CreateAsync();
        Fake.Down = true;

        await Should.ThrowAsync<HttpRequestException>(() => CreateAsync(key: "order:down:attempt:1"));
        await Should.ThrowAsync<HttpRequestException>(() => Gateway.GetStatusAsync(session.SessionId, Ct));
        await Should.ThrowAsync<HttpRequestException>(() => Gateway.RefundAsync(session.SessionId, new Money(1_000), "refund:down", Ct));
    }
}
