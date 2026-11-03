using System.Text.Json;
using FluentValidation;
using TicketRush.Domain.Primitives;

namespace TicketRush.Api.Common.Http;

public static class ValidationExtensions
{
    private const string DefaultCode = "validation.failed";

    /// <returns>A 400 ProblemDetails with per-field errors, or null when the command is valid.</returns>
    public static async Task<IResult?> ValidateToProblemAsync<T>(this IValidator<T> validator, T command, CancellationToken ct)
    {
        var result = await validator.ValidateAsync(command, ct);
        if (result.IsValid)
        {
            return null;
        }

        // Rules may carry a domain code (e.g. auth.weak_password); FluentValidation's own codes have no dot.
        var code = result.Errors.Select(e => e.ErrorCode).FirstOrDefault(c => c?.Contains('.', StringComparison.Ordinal) == true)
            ?? DefaultCode;
        var details = result.Errors
            .GroupBy(e => JsonNamingPolicy.CamelCase.ConvertName(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        return Error.Validation(code, "One or more fields are invalid.", details).ToProblem();
    }
}
