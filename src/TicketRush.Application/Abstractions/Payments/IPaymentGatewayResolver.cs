namespace TicketRush.Application.Abstractions.Payments;

/// <summary>Finds the gateway for a provider name (the webhook route) or the configured one (the pay endpoint).</summary>
public interface IPaymentGatewayResolver
{
    /// <summary>The gateway selected by <c>Payment:Provider</c>.</summary>
    IPaymentGateway Default { get; }

    /// <summary>The gateway registered under <paramref name="provider"/> (case-insensitive), or <c>null</c> when unknown.</summary>
    IPaymentGateway? Get(string provider);
}
