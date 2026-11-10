using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Payments;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Api.IntegrationTests.Payments;

internal sealed class FakeEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "test";

    public string ContentRootPath { get; set; } = ".";

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
}

/// <summary>Startup guards (the validators run with <c>ValidateOnStart</c>) and the registry/keys of the payment pieces.</summary>
public sealed class PaymentConfigurationTests
{
    private const string Secret = "a-long-enough-webhook-secret";

    private static ValidateOptionsResult Validate(string environment, Action<MockGatewayOptions> configure)
    {
        var options = new MockGatewayOptions { WebhookSecret = Secret };
        configure(options);
        return new MockGatewayOptionsValidator(new FakeEnvironment(environment)).Validate(null, options);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_mock_control_surface_is_off_by_default_everywhere(string environment)
    {
        new MockGatewayOptions().Enabled.ShouldBeFalse();
        Validate(environment, _ => { }).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Test")]
    public void Enabling_the_mock_outside_Development_fails_startup(string environment)
    {
        var result = Validate(environment, o => o.Enabled = true);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Payments:Mock:Enabled must not be true outside Development");
        result.FailureMessage.ShouldContain(environment);
        result.FailureMessage.ShouldContain("AllowInProduction");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void An_explicit_override_allows_the_mock_outside_Development(string environment)
    {
        Validate(environment, o => { o.Enabled = true; o.AllowInProduction = true; }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void The_override_alone_enables_nothing()
    {
        new MockGatewayOptions { AllowInProduction = true }.Enabled.ShouldBeFalse();
    }

    [Fact]
    public void Enabling_the_mock_in_Development_is_fine()
    {
        Validate("Development", o => o.Enabled = true).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void The_webhook_secret_is_required_outside_Development(string environment)
    {
        Validate(environment, o => o.WebhookSecret = string.Empty).Failed.ShouldBeTrue();
        Validate(environment, o => o.WebhookSecret = "short").Failed.ShouldBeTrue();
        Validate(environment, o => o.WebhookSecret = Secret).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("Development", "")]
    [InlineData("Development", "   ")]
    [InlineData("Staging", "")]
    [InlineData("Production", "")]
    public void An_enabled_mock_needs_a_secret_in_every_environment(string environment, string secret)
    {
        var result = Validate(environment, o =>
        {
            o.Enabled = true;
            o.AllowInProduction = true;
            o.WebhookSecret = secret;
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("WebhookSecret is required when Payments:Mock:Enabled is true");
    }

    [Fact]
    public void An_enabled_mock_with_a_short_development_secret_is_fine()
    {
        Validate("Development", o => { o.Enabled = true; o.WebhookSecret = "dev"; }).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Development_may_run_without_a_secret()
    {
        Validate("Development", o => o.WebhookSecret = string.Empty).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://web.test")]
    [InlineData("/relative")]
    public void Urls_must_be_absolute_http(string url)
    {
        Validate("Development", o => o.WebBaseUrl = url).Failed.ShouldBeTrue();
        Validate("Development", o => o.WebhookUrl = url).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Delays_must_be_positive()
    {
        Validate("Development", o => o.DelayedWebhookDelay = TimeSpan.Zero).Failed.ShouldBeTrue();
        Validate("Development", o => o.DeliveryRetryBaseDelay = TimeSpan.FromSeconds(-1)).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Defaults_match_the_spec()
    {
        var defaults = new MockGatewayOptions();

        defaults.DelayedWebhookDelay.ShouldBe(TimeSpan.FromMinutes(3));
        defaults.WebhookUrl.ShouldBe("http://api:8080/api/v1/webhooks/payments/mock");
        defaults.SimulateOutage.ShouldBeFalse();
    }

    [Fact]
    public void Redis_keys_are_namespaced_per_environment_and_hash_caller_chosen_ids()
    {
        var keys = new RedisKeys(Options.Create(new RedisOptions { Environment = "x" }));

        keys.PayMockSession("mock_abc").ToString().ShouldBe("tr:x:pay:mock:session:mock_abc");
        keys.PayMockDeliveries.ToString().ShouldBe("tr:x:pay:mock:deliveries");
        keys.PayMockIdempotency("key with spaces and : colons").ToString().ShouldMatch("^tr:x:pay:mock:idem:[0-9a-f]{64}$");
        keys.PayMockRefund("order-1").ToString().ShouldMatch("^tr:x:pay:mock:refund:[0-9a-f]{64}$");
        keys.PayMockIdempotency("a").ToString().ShouldNotBe(keys.PayMockIdempotency("b").ToString());
        keys.PayMockIdempotency("a").ToString().ShouldNotBe(keys.PayMockRefund("a").ToString());
    }

    [Fact]
    public void The_resolver_finds_gateways_by_provider_name_ignoring_case()
    {
        var mock = new StubGateway("mock");
        var resolver = new PaymentGatewayResolver([mock], Options.Create(new PaymentOptions { Provider = "Mock" }));

        resolver.Get("MOCK").ShouldBeSameAs(mock);
        resolver.Get("mock").ShouldBeSameAs(mock);
        resolver.Get("vnpay").ShouldBeNull();
        resolver.Default.ShouldBeSameAs(mock);
    }

    [Fact]
    public void The_default_gateway_must_be_registered()
    {
        var resolver = new PaymentGatewayResolver([new StubGateway("mock")], Options.Create(new PaymentOptions { Provider = "VnPay" }));

        Should.Throw<InvalidOperationException>(() => resolver.Default);
    }

    private sealed class StubGateway(string provider) : Application.Abstractions.Payments.IPaymentGateway
    {
        public string Provider => provider;

        public Task<Application.Abstractions.Payments.PaymentSession> CreateSessionAsync(Domain.Orders.Order order, Uri returnUrl, DateTimeOffset expiresAt, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Application.Abstractions.Payments.PaymentStatus> GetStatusAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();

        public Task<Application.Abstractions.Payments.RefundResult> RefundAsync(string sessionId, Domain.Primitives.Money amount, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Application.Abstractions.Payments.WebhookEvent VerifyAndParse(Application.Abstractions.Payments.WebhookHeaders headers, string body) =>
            throw new NotSupportedException();
    }
}
