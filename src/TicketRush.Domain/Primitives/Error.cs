namespace TicketRush.Domain.Primitives;

public enum ErrorType
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    RateLimited,
    Unavailable,
}

/// <param name="Code">Stable, machine-readable code such as <c>seat.already_held</c>.</param>
/// <param name="Message">Human readable message, safe to show to end users.</param>
/// <param name="Type">Category that decides the HTTP status code.</param>
/// <param name="Details">Optional structured details (e.g. validation failures per field).</param>
/// <param name="Extensions">Optional extra members of the problem body, e.g. <c>unavailable: int[]</c> (spec 07).</param>
public sealed record Error(
    string Code,
    string Message,
    ErrorType Type,
    IReadOnlyDictionary<string, string[]>? Details = null,
    IReadOnlyDictionary<string, object?>? Extensions = null)
{
    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorType.Validation, details);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorType.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorType.Forbidden);

    public static Error NotFound(string code, string message) => new(code, message, ErrorType.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorType.Conflict);

    public static Error RateLimited(string code, string message) => new(code, message, ErrorType.RateLimited);

    public static Error Unavailable(string code, string message) => new(code, message, ErrorType.Unavailable);
}

public static class CommonErrors
{
    /// <summary>
    /// <see cref="Error.Extensions"/> key (an <c>int</c> of seconds) of a <see cref="ErrorType.RateLimited"/> error that says how long
    /// to wait; the API also sends it as the <c>Retry-After</c> header.
    /// </summary>
    public const string RetryAfterExtension = "retryAfterSeconds";

    public static readonly Error SystemBusy =
        Error.Unavailable("system.busy", "The system is busy. Please try again in a moment.");
}
