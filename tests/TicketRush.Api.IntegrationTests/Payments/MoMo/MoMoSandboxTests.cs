using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// Spec 18: our signature strings against MoMo itself. Calls MoMo's public sandbox (test-payment.momo.vn) with MoMo's published
/// test keys, so it only runs when asked: <c>MOMO_SANDBOX_LIVE=1</c>. Not part of CI (it needs the internet and MoMo up).
/// </summary>
public sealed class MoMoSandboxTests(ContainersFixture containers)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_real_sandbox_payment_is_created_and_reads_as_pending()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MOMO_SANDBOX_LIVE") == "1", "Set MOMO_SANDBOX_LIVE=1 to call MoMo's sandbox.");
        var options = new MoMoOptions
        {
            Endpoint = FakeMoMo.Endpoint,
            PartnerCode = FakeMoMo.PartnerCode,
            AccessKey = FakeMoMo.AccessKey,
            SecretKey = FakeMoMo.SecretKey,
            IpnUrl = "https://example.com/api/v1/webhooks/payments/momo",
        };
        var services = new ServiceCollection();
        services.AddHttpClient(MoMoClient.HttpClientName, c =>
        {
            c.BaseAddress = new Uri(options.Endpoint);
            c.Timeout = TimeSpan.FromSeconds(40);
        });
        await using var provider = services.BuildServiceProvider();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(containers.RedisConnectionString);
        var monitor = new StaticOptionsMonitor<MoMoOptions>(options);
        var keys = new RedisKeys(Microsoft.Extensions.Options.Options.Create(new RedisOptions { Environment = $"live{Guid.NewGuid():N}"[..12] }));
        var gateway = new MoMoPaymentGateway(new MoMoClient(provider.GetRequiredService<IHttpClientFactory>(), monitor), new MoMoSessionCache(redis, keys), monitor, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var order = Order.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            [new OrderItem(Guid.NewGuid(), null, new Money(10_000), 1)],
            new Money(10_000),
            new BuyerInfo("Nguyen Van A", "a@example.com", null),
            now.AddMinutes(8),
            now);

        var session = await gateway.CreateSessionAsync(order, new Uri("https://example.com/checkout/x/result"), now.AddMinutes(6), $"live:{Guid.NewGuid():N}", Ct);

        TestContext.Current.SendDiagnosticMessage($"MoMo sandbox session {session.SessionId}: {session.RedirectUrl}");
        session.RedirectUrl.Host.ShouldBe("test-payment.momo.vn");
        (await gateway.GetStatusAsync(session.SessionId, Ct)).ShouldBe(PaymentStatus.Pending);
        (await gateway.GetStatusAsync("TR" + Guid.NewGuid().ToString("N"), Ct)).ShouldBe(PaymentStatus.Expired);
    }
}
