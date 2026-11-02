using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Common.Http;

/// <summary>Maps use-case results to HTTP. Every endpoint goes through here so error shapes stay uniform.</summary>
public static class ResultExtensions
{
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();

    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem();

    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess) =>
        result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();

    public static IResult ToProblem(this Error error)
    {
        var status = StatusCodeFor(error.Type);
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };

        if (error.Type == ErrorType.Validation && error.Details is not null)
        {
            return TypedResults.ValidationProblem(
                error.Details,
                title: error.Message,
                type: TypeFor(status),
                extensions: extensions);
        }

        if (error.Details is not null)
        {
            extensions["details"] = error.Details;
        }

        return TypedResults.Problem(
            statusCode: status,
            title: error.Message,
            type: TypeFor(status),
            extensions: extensions);
    }

    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        ErrorType.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorType.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static string TypeFor(int status) => $"https://httpstatuses.io/{status}";
}
