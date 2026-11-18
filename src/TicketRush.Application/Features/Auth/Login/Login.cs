using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;

namespace TicketRush.Application.Features.Auth.Login;

/// <param name="CaptchaToken">Turnstile token (spec 12), needed after 3 failed sign-ins for this email in 15 minutes.</param>
public sealed record LoginCommand(string Email, string Password, string? CaptchaToken = null);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(256);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(256);
    }
}

/// <summary>Unconfirmed emails may sign in; buying and queueing check confirmation (spec 03 rules).</summary>
public sealed class LoginHandler(
    IIdentityService identity,
    IAccessTokenIssuer issuer,
    IAppDbContext db,
    ICaptchaVerifier captcha,
    ILoginFailureCounter failures,
    TimeProvider clock)
{
    /// <summary>Failed sign-ins (per email, existing or not) after which a CAPTCHA is needed (spec 12).</summary>
    public const int CaptchaAfterFailures = 3;

    public async Task<Result<AuthSession>> HandleAsync(LoginCommand command, ClientInfo client, CancellationToken ct)
    {
        var email = command.Email.Trim();
        if (await failures.CountAsync(email, ct) >= CaptchaAfterFailures)
        {
            if (string.IsNullOrWhiteSpace(command.CaptchaToken))
            {
                return CaptchaErrors.Required;
            }

            if (CaptchaErrors.For(await captcha.VerifyAsync(command.CaptchaToken, ct)) is { } captchaError)
            {
                return captchaError;
            }
        }

        var check = await identity.CheckPasswordAsync(email, command.Password, ct);
        if (check.IsFailure)
        {
            if (check.Error.Code == AuthErrors.InvalidCredentials.Code || check.Error.Code == AuthErrors.LockedOut.Code)
            {
                await failures.RecordAsync(email, ct);
            }

            return check.Error;
        }

        await failures.ResetAsync(email, ct);

        var user = check.Value;
        var (refreshToken, raw) = db.StartFamily(user.Id, clock.GetUtcNow(), client);
        await db.SaveChangesAsync(ct);
        return new AuthSession(issuer.Issue(user), raw, refreshToken.ExpiresAt, user);
    }
}
