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
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<GatewayResilience>())));
        services.AddSingleton<IPaymentGatewayResolver, PaymentGatewayResolver>();

        // Deliveries have their own backoff and attempt limit; the host-wide resilience defaults would stack a second layer on top.
#pragma warning disable EXTEXP0001 // Experimental only in name; it is the supported way to opt a client out of ConfigureHttpClientDefaults.
        services.AddHttpClient<MockWebhookDeliverer>(client => client.Timeout = TimeSpan.FromSeconds(10))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        return services;
    }
}
