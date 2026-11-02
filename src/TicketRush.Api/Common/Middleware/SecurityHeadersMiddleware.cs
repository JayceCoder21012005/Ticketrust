namespace TicketRush.Api.Common.Middleware;

/// <summary>The API only serves JSON, so its CSP can deny everything.</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            return Task.CompletedTask;
        });

        return next(context);
    }
}
