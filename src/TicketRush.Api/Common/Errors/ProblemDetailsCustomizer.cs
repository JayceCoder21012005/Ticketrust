using TicketRush.Api.Common.Middleware;

namespace TicketRush.Api.Common.Errors;

internal static class ProblemDetailsCustomizer
{
    /// <summary>Every problem response carries the correlation id so support can find the logs.</summary>
    public static void Customize(ProblemDetailsContext context)
    {
        if (CorrelationIdMiddleware.TryGet(context.HttpContext, out var correlationId))
        {
            context.ProblemDetails.Extensions["correlationId"] = correlationId;
        }

        context.ProblemDetails.Instance ??= context.HttpContext.Request.Path;
    }
}
