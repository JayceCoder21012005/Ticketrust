using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Common.Errors;

/// <summary>
/// Last line of defence: turns unhandled exceptions into RFC 9457 ProblemDetails. Stack traces are only
/// included in Development; everywhere else clients get a generic message plus <c>traceId</c> to quote.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = Classify(exception);

        if (status >= StatusCodes.Status500InternalServerError)
        {
            LogUnhandled(logger, httpContext.Request.Method, httpContext.Request.Path, exception);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = $"https://httpstatuses.io/{status}",
            Extensions = { ["code"] = code },
        };

        if (environment.IsDevelopment())
        {
            problem.Detail = exception.ToString();
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static (int Status, string Code, string Title) Classify(Exception exception) => exception switch
    {
        DomainException domain => (StatusCodes.Status409Conflict, domain.Code, domain.Message),
        BadHttpRequestException badRequest => (badRequest.StatusCode, "request.invalid", "The request is invalid."),
        _ => (StatusCodes.Status500InternalServerError, "server.error", "An unexpected error occurred."),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, string method, PathString path, Exception exception);
}
