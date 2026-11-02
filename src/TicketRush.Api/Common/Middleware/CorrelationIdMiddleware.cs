using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace TicketRush.Api.Common.Middleware;

/// <summary>
/// Accepts a client <c>X-Correlation-Id</c> (or creates one), echoes it on the response, adds it to the
/// logging scope and the current trace, so one id follows a request through web → api → worker.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    private const string ItemKey = "CorrelationId";

    public static bool TryGet(HttpContext context, [NotNullWhen(true)] out string? correlationId)
    {
        correlationId = context.Items[ItemKey] as string;
        return correlationId is not null;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ReadOrCreate(context);
        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        Activity.Current?.SetTag("correlation.id", correlationId);
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    private static string ReadOrCreate(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        // Only accept short, safe ids: the value is echoed back and written to logs (no header/log injection).
        return SafeId().IsMatch(incoming) ? incoming : Guid.CreateVersion7().ToString("N");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeId();
}
