using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Payments;

/// <summary>Looks gateways up by <see cref="IPaymentGateway.Provider"/>; the default is <c>Payment:Provider</c>.</summary>
public sealed class PaymentGatewayResolver(IEnumerable<IPaymentGateway> gateways, IOptions<PaymentOptions> options) : IPaymentGatewayResolver
{
    private readonly Dictionary<string, IPaymentGateway> _byProvider =
        gateways.ToDictionary(g => g.Provider, StringComparer.OrdinalIgnoreCase);

    public IPaymentGateway Default => Get(options.Value.Provider)
        ?? throw new InvalidOperationException($"Payment:Provider '{options.Value.Provider}' has no registered gateway.");

    public IPaymentGateway? Get(string provider) => _byProvider.GetValueOrDefault(provider);
}
