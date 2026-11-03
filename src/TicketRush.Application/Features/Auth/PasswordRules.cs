using FluentValidation;

namespace TicketRush.Application.Features.Auth;

/// <summary>Spec 03: at least 10 characters with letters and digits, and not the email itself.</summary>
public static class PasswordRules
{
    public const int MinLength = 10;
    public const string WeakPasswordCode = "auth.weak_password";

    public static IRuleBuilderOptions<T, string> StrongPassword<T>(this IRuleBuilderInitial<T, string> rule, Func<T, string> email) =>
        rule.Cascade(CascadeMode.Stop)
            .NotEmpty().WithErrorCode(WeakPasswordCode)
            .MinimumLength(MinLength).WithErrorCode(WeakPasswordCode)
            .Must(p => p.Any(char.IsLetter)).WithErrorCode(WeakPasswordCode).WithMessage("Password must contain a letter.")
            .Must(p => p.Any(char.IsDigit)).WithErrorCode(WeakPasswordCode).WithMessage("Password must contain a digit.")
            .Must((command, p) => !string.Equals(p, email(command), StringComparison.OrdinalIgnoreCase))
            .WithErrorCode(WeakPasswordCode).WithMessage("Password must not be your email.");
}
