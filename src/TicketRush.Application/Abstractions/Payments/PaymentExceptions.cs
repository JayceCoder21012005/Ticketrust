using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Payments;

/// <summary>Bad signature, bad header or timestamp outside <see cref="WebhookTolerance.Window"/>. The webhook endpoint answers 401.</summary>
public sealed class InvalidWebhookSignatureException : Exception
{
    public InvalidWebhookSignatureException()
    {
    }

    public InvalidWebhookSignatureException(string message)
        : base(message)
    {
    }

    public InvalidWebhookSignatureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Authentic webhook whose body is not an event we understand. The webhook endpoint answers 400.</summary>
public sealed class InvalidWebhookPayloadException : Exception
{
    public InvalidWebhookPayloadException()
    {
    }

    public InvalidWebhookPayloadException(string message)
        : base(message)
    {
    }

    public InvalidWebhookPayloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The gateway refused the request itself (not a transient fault): retrying cannot help and it must not trip the
/// circuit breaker. Callers treat it as a business failure.
/// </summary>
public sealed class PaymentRejectedException : Exception
{
    public PaymentRejectedException()
    {
    }

    public PaymentRejectedException(string message)
        : base(message)
    {
    }

    public PaymentRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The gateway could not be reached: retries are exhausted, a call timed out or the circuit breaker is open.
/// Maps to <see cref="PaymentErrors.GatewayUnavailable"/> (503); holds and orders are left untouched.
/// </summary>
public sealed class PaymentGatewayUnavailableException : Exception
{
    public PaymentGatewayUnavailableException()
    {
    }

    public PaymentGatewayUnavailableException(string message)
        : base(message)
    {
    }

    public PaymentGatewayUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static class PaymentErrors
{
    public static readonly Error GatewayUnavailable =
        Error.Unavailable("payment.gateway_unavailable", "The payment provider is temporarily unavailable. Your seats are still held, please try again shortly.");
}
