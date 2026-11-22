using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Infrastructure.Payments;

public static class PaymentsServiceCollectionExtensions
{
    /// <summary>
    /// Payment gateways (spec 08) for api and worker: options with the Production guard, the mock gateway behind its
    /// resilience decorator, the gateway resolver, and the mock's webhook delivery client. Whether the api maps the mock
    /// control endpoints and the worker delivers webhooks is decided by <see cref="MockGatewayOptions.Enabled"/> in those hosts.
    /// </summary>
    public static IServiceCollection AddPaymentGateways(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<MockGatewayOptions>>(new MockGatewayOptionsValidator(environment));
        services.AddOptions<MockGatewayOptions>()
            .BindConfiguration(MockGatewayOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddValidatedOptions<GatewayResilienceOptions>(GatewayResilienceOptions.SectionName);

        services.AddSingleton<MockWebhookQueue>();
        services.AddSingleton<MockSessionStore>();
        services.AddSingleton<MockGatewayControl>();
        services.AddSingleton<MockPaymentGateway>();
        services.AddSingleton<IPaymentGateway>(provider => new ResilientPaymentGateway(
            provider.GetRequiredService<MockPaymentGateway>(),
            new GatewayResilience(
                MockPaymentGateway.ProviderName,
                provider.GetRequiredService<IOptions<GatewayResilienceOptions>>().Value,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayResilience>()),
            provider.GetRequiredService<Application.Abstractions.Observability.ISalesMetrics>()));
        AddMoMo(services, environment);
        services.AddSingleton<IPaymentGatewayResolver, PaymentGatewayResolver>();

        // Deliveries have their own backoff and attempt limit; the host-wide resilience defaults would stack a second layer on top.
#pragma warning disable EXTEXP0001 // Experimental only in name; it is the supported way to opt a client out of ConfigureHttpClientDefaults.
        services.AddHttpClient<MockWebhookDeliverer>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        return services;
    }

    /// <summary>
    /// Spec 18: the MoMo adapter, always registered (its options are only demanded when <c>Payment:Provider</c> is MoMo) and
    /// behind the same resilience decorator as the mock, with MoMo's own per-attempt timeout (MoMo asks for at least 30 s).
    /// </summary>
    private static void AddMoMo(IServiceCollection services, IHostEnvironment environment)
    {
        services.AddSingleton<IValidateOptions<MoMo.MoMoOptions>>(provider =>
            new MoMo.MoMoOptionsValidator(environment, provider.GetRequiredService<IOptions<Options.PaymentOptions>>()));
        services.AddOptions<MoMo.MoMoOptions>().BindConfiguration(MoMo.MoMoOptions.SectionName).ValidateOnStart();

        // The decorator retries; the host-wide resilience defaults would stack a second layer on top.
#pragma warning disable EXTEXP0001 // Experimental only in name; it is the supported way to opt a client out of ConfigureHttpClientDefaults.
        services.AddHttpClient(MoMo.MoMoClient.HttpClientName, (provider, client) =>
            {
                var options = provider.GetRequiredService<IOptions<MoMo.MoMoOptions>>().Value;
                client.BaseAddress = new Uri(options.Endpoint);
                client.Timeout = options.Timeout + TimeSpan.FromSeconds(5);
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<MoMo.MoMoClient>();
        services.AddSingleton<MoMo.MoMoSessionCache>();
        services.AddSingleton<MoMo.MoMoPaymentGateway>();
        services.AddSingleton<IPaymentGateway>(provider =>
        {
            var shared = provider.GetRequiredService<IOptions<GatewayResilienceOptions>>().Value;
            var momo = provider.GetRequiredService<IOptions<MoMo.MoMoOptions>>().Value;
            return new ResilientPaymentGateway(
                provider.GetRequiredService<MoMo.MoMoPaymentGateway>(),
                new GatewayResilience(
                    MoMo.MoMoPaymentGateway.ProviderName,
                    new GatewayResilienceOptions
                    {
                        Timeout = momo.Timeout,
                        RetryCount = shared.RetryCount,
                        RetryBaseDelay = shared.RetryBaseDelay,
                        BreakerFailureRatio = shared.BreakerFailureRatio,
                        BreakerMinimumThroughput = shared.BreakerMinimumThroughput,
                        BreakerSamplingDuration = shared.BreakerSamplingDuration,
                        BreakerBreakDuration = shared.BreakerBreakDuration,
                    },
                    provider.GetRequiredService<TimeProvider>(),
                    provider.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayResilience>()),
                provider.GetRequiredService<Application.Abstractions.Observability.ISalesMetrics>());
        });
    }
}
