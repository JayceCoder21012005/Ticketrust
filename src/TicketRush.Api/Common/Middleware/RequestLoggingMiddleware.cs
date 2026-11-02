using System.Diagnostics;

namespace TicketRush.Api.Common.Middleware;

/// <summary>
/// One log line per request: method, path, status, duration. Never bodies, headers or the query string,
/// which can carry tokens (e.g. SignalR's <c>access_token</c>) or PII.
/// </summary>
public sealed partial class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            LogRequest(logger, context.Request.Method, context.Request.Path, context.Response.StatusCode, elapsed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "HTTP {Method} {Path} responded {StatusCode} in {ElapsedMs:0.0} ms")]
    private static partial void LogRequest(ILogger logger, string method, PathString path, int statusCode, double elapsedMs);
}
