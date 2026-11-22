using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Orders;
using TicketRush.Domain.Events;
using TicketRush.Domain.Orders;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Payments.MoMo;
using TicketRush.Infrastructure.Persistence;
using TicketRush.Infrastructure.Persistence.Seed;

namespace TicketRush.Api.IntegrationTests.Payments.MoMo;

/// <summary>
/// Spec 18 over HTTP with MoMo as the provider (a fake MoMo plays its side): pay redirects to MoMo, the buyer's return confirms
/// the payment by asking MoMo, and the IPN for the same payment then decides nothing twice (and the other way round).
/// </summary>
public sealed class ConfirmPaymentTests(ContainersFixture containers) : IAsyncLifetime
{
    private readonly FakeMoMo _momo = new();
    private ApiFactory _factory = null!;
    private WebApplicationFactory<Program> _host = null!;
    private HttpClient _client = null!;
    private HttpClient _authClient = null!;
    private AuthApi _auth = null!;
    private Guid _event;
    private Guid[] _gaTiers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var settings = MoMoSettings.For();
        settings["Payments:Resilience:RetryCount"] = "1";
        settings["Payments:Resilience:RetryBaseDelay"] = "00:00:00";
        settings["Payments:Resilience:BreakerMinimumThroughput"] = "100";
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, settings: settings);
        _authClient = AuthApi.CreateClient(_factory);
        _auth = new AuthApi(_factory, _authClient);
        await using var scope = _factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevDataSeeder>().SeedAsync(Ct);
        var evt = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Events.AsNoTracking().Include(e => e.Tiers)
            .SingleAsync(e => e.Slug == "son-tung-mtp-live", Ct);
        _event = evt.Id;
        _gaTiers = [.. evt.Tiers.Where(t => t.Kind == TicketKind.GeneralAdmission).Select(t => t.Id)];
        await scope.ServiceProvider.GetRequiredService<IInventorySeeder>().SeedAsync(_event, Ct);

        _host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            s.AddHttpClient(MoMoClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _momo)));
        _client = _host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _authClient.Dispose();
        await _host.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private sealed record Buyer(Guid UserId, string Access);

    private sealed record Paying(Buyer Buyer, Guid OrderId, string SessionId, long Total, Uri RedirectUrl);

    private async Task<Buyer> NewBuyerAsync()
    {
        var email = $"buyer-{Guid.NewGuid():N}@example.com";
        var access = (await _auth.SignInWithRoleAsync(email, "Customer")).AccessToken;
        using var scope = _factory.Services.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email);
        return new Buyer(user!.Id, access);
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? access, object? body = null, string? key = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (access is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        }

        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return _client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(status, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A buyer with a hold of two GA tickets, an order for it, and "Thanh toán" pressed: the order awaits MoMo.</summary>
    private async Task<Paying> PayAsync()
    {
        var buyer = await NewBuyerAsync();
        var store = _factory.Services.GetRequiredService<IInventoryStore>();
        var request = new HoldRequest(_event, buyer.UserId, [], [new GaLine(_gaTiers[0], 2)], _gaTiers, 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));
        (await store.CreateHoldAsync(request, Ct)).Status.ShouldBe(HoldStatus.Ok);
        var hold = (await store.GetUserHoldAsync(_event, buyer.UserId, Ct))!;
        var body = new { eventId = _event, holdId = hold.HoldId, buyer = new { name = "Nguyễn Văn An", email = "an@example.com", phone = "0901234567" } };
        var order = await JsonAsync(await SendAsync(HttpMethod.Post, "/api/v1/orders", buyer.Access, body, Guid.NewGuid().ToString()), HttpStatusCode.Created);
        var orderId = order.GetProperty("id").GetGuid();
        var pay = await JsonAsync(await SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/pay", buyer.Access, key: Guid.NewGuid().ToString()));
        var attempt = await WithDbAsync(db => db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.OrderId == orderId, Ct));
        return new Paying(buyer, orderId, attempt.ProviderSessionId, order.GetProperty("total").GetInt64(), new Uri(pay.GetProperty("redirectUrl").GetString()!));
    }

    private Task<HttpResponseMessage> ConfirmAsync(Paying paying, Buyer? who = null) =>
        SendAsync(HttpMethod.Post, $"/api/v1/orders/{paying.OrderId}/payment/confirm", (who ?? paying.Buyer).Access);

    private Task<HttpResponseMessage> IpnAsync(Paying paying, long transId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/momo")
        {
            Content = new StringContent(FakeMoMo.SignedIpn(paying.SessionId, paying.Total, transId, 0, DateTimeOffset.UtcNow), Encoding.UTF8, "application/json"),
        };
        return _client.SendAsync(request, Ct);
    }

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private Task<(OrderStatus Status, int Tickets, int RefundPending)> StateAsync(Guid orderId) => WithDbAsync(async db =>
    {
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId, Ct);
        var refunds = await db.PaymentAttempts.CountAsync(a => a.OrderId == orderId && a.Status == PaymentAttemptStatus.RefundPending, Ct);
        return (order.Status, await db.Tickets.CountAsync(t => t.OrderId == orderId, Ct), refunds);
    });

    [Fact]
    public async Task Pay_with_MoMo_returns_MoMo_payUrl()
    {
        var paying = await PayAsync();

        paying.RedirectUrl.Host.ShouldBe("test-payment.momo.vn");
        paying.SessionId.ShouldStartWith("TR");
        _momo.Knows(paying.SessionId).ShouldBeTrue();
        _momo.CreateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Confirm_after_MoMo_reports_success_pays_the_order()
    {
        var paying = await PayAsync();
        _momo.Pay(paying.SessionId);

        var order = await JsonAsync(await ConfirmAsync(paying));

        order.GetProperty("status").GetString().ShouldBe("Paid");
        (await StateAsync(paying.OrderId)).ShouldBe((OrderStatus.Paid, 2, 0));
    }

    [Fact]
    public async Task Confirm_then_IPN_for_the_same_payment_changes_nothing()
    {
        var paying = await PayAsync();
        var transId = _momo.Pay(paying.SessionId);
        await JsonAsync(await ConfirmAsync(paying));

        using var ipn = await IpnAsync(paying, transId);

        ipn.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await StateAsync(paying.OrderId)).ShouldBe((OrderStatus.Paid, 2, 0));
    }

    [Fact]
    public async Task IPN_then_confirm_changes_nothing()
    {
        var paying = await PayAsync();
        var transId = _momo.Pay(paying.SessionId);
        using var ipn = await IpnAsync(paying, transId);
        ipn.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var order = await JsonAsync(await ConfirmAsync(paying));

        order.GetProperty("status").GetString().ShouldBe("Paid");
        (await StateAsync(paying.OrderId)).ShouldBe((OrderStatus.Paid, 2, 0));
    }

    [Fact]
    public async Task Confirm_while_pending_changes_nothing()
    {
        var paying = await PayAsync();

        var order = await JsonAsync(await ConfirmAsync(paying));

        order.GetProperty("status").GetString().ShouldBe("AwaitingPayment");
        (await StateAsync(paying.OrderId)).ShouldBe((OrderStatus.AwaitingPayment, 0, 0));
    }

    [Fact]
    public async Task Confirm_with_MoMo_down_returns_the_order_unchanged()
    {
        var paying = await PayAsync();
        _momo.Pay(paying.SessionId);
        _momo.Down = true;

        var order = await JsonAsync(await ConfirmAsync(paying));

        order.GetProperty("status").GetString().ShouldBe("AwaitingPayment");
    }

    [Fact]
    public async Task Confirm_on_someone_elses_order_is_404()
    {
        var paying = await PayAsync();
        _momo.Pay(paying.SessionId);

        using var response = await ConfirmAsync(paying, await NewBuyerAsync());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StateAsync(paying.OrderId)).Status.ShouldBe(OrderStatus.AwaitingPayment);
    }

    [Fact]
    public async Task Expiry_sweep_fulfils_a_MoMo_payment_the_IPN_never_reached()
    {
        var paying = await PayAsync();
        _momo.Pay(paying.SessionId);
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE orders SET expires_at = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE id = {paying.OrderId}", Ct));

        await using var scope = _host.Services.CreateAsyncScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<ExpireOrderHandler>().HandleAsync(paying.OrderId, new HashSet<string>(), Ct);

        outcome.ShouldBe(OrderExpiryOutcome.FulfilledLate);
        (await StateAsync(paying.OrderId)).Status.ShouldBe(OrderStatus.Paid);
    }

    [Fact]
    public async Task Pay_with_MoMo_down_is_503()
    {
        var buyer = await NewBuyerAsync();
        var store = _factory.Services.GetRequiredService<IInventoryStore>();
        var request = new HoldRequest(_event, buyer.UserId, [], [new GaLine(_gaTiers[0], 1)], _gaTiers, 8, false, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(8));
        (await store.CreateHoldAsync(request, Ct)).Status.ShouldBe(HoldStatus.Ok);
        var hold = (await store.GetUserHoldAsync(_event, buyer.UserId, Ct))!;
        var body = new { eventId = _event, holdId = hold.HoldId, buyer = new { name = "Nguyễn Văn An", email = "an@example.com", phone = "0901234567" } };
        var order = await JsonAsync(await SendAsync(HttpMethod.Post, "/api/v1/orders", buyer.Access, body, Guid.NewGuid().ToString()), HttpStatusCode.Created);
        _momo.Down = true;

        using var pay = await SendAsync(HttpMethod.Post, $"/api/v1/orders/{order.GetProperty("id").GetGuid()}/pay", buyer.Access, key: Guid.NewGuid().ToString());

        pay.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
