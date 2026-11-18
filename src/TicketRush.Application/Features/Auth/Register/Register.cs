using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Auth.Register;

/// <param name="CaptchaToken">Turnstile token (spec 12).</param>
/// <param name="Website">Honeypot (spec 12): a hidden field people never fill. Filled → a generic success and nothing created.</param>
public sealed record RegisterCommand(string Email, string Password, string FullName, string? CaptchaToken = null, string? Website = null);

public sealed class RegisterValidator : AbstractValidator<RegisterCommand>
{
    public RegisterValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.FullName).NotEmpty().MaximumLength(100).Must(n => !string.IsNullOrWhiteSpace(n));
        RuleFor(c => c.Password).StrongPassword(c => c.Email);
    }
}

public sealed class RegisterHandler(IIdentityService identity, ICaptchaVerifier captcha)
{
    public async Task<Result> HandleAsync(RegisterCommand command, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(command.Website))
        {
            // Same answer as a real registration (202, "check your email"), so the bot learns nothing.
            return Result.Success();
        }

        if (CaptchaErrors.For(await captcha.VerifyAsync(command.CaptchaToken, ct)) is { } captchaError)
        {
            return captchaError;
        }

        return await identity.RegisterAsync(command.Email.Trim(), command.Password, command.FullName.Trim(), ct);
    }
}
