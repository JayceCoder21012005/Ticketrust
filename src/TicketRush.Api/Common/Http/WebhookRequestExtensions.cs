using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Api.Common.Http;

public static class WebhookRequestExtensions
{
    /// <summary>
    /// Copies the request headers for <see cref="IPaymentGateway.VerifyAndParse"/> (the Application layer cannot see
    /// <see cref="HttpRequest"/>). Repeated headers are joined with commas, as ASP.NET Core does.
    /// </summary>
    public static WebhookHeaders ToWebhookHeaders(this HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new WebhookHeaders(request.Headers.Select(h => KeyValuePair.Create(h.Key, h.Value.ToString())));
    }
}
