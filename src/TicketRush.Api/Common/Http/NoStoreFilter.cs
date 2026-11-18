namespace TicketRush.Api.Common.Http;

/// <summary>
/// Marks every answer of the endpoints it guards, errors included, as <c>Cache-Control: no-store</c>: the body is
/// per-user (and for <c>qr-key</c> a credential), so no browser, proxy or service worker may keep it.
/// </summary>
public sealed class NoStoreFilter : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        return next(context);
    }
}
