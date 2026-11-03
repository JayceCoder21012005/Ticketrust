using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketRush.Infrastructure.Identity;

/// <summary>
/// The rules Identity's options can't express (spec 03): a letter is required and the password must not be
/// the email. Mirrors <c>PasswordRules</c> in Application, so a bypassed validator still can't store a weak password.
/// </summary>
internal sealed class LetterAndNotEmailPasswordValidator : IPasswordValidator<AppUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password)
    {
        var errors = new List<IdentityError>();
        if (password is null || !password.Any(char.IsLetter))
        {
            errors.Add(new IdentityError { Code = "PasswordRequiresLetter", Description = "Password must contain a letter." });
        }

        if (password is not null && string.Equals(password, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new IdentityError { Code = "PasswordEqualsEmail", Description = "Password must not be your email." });
        }

        return Task.FromResult(errors.Count == 0 ? IdentityResult.Success : IdentityResult.Failed([.. errors]));
    }
}

/// <summary>Confirmation links live longer (3 days) than reset links (1 hour, the default provider).</summary>
internal sealed class EmailConfirmationTokenProvider(
    IDataProtectionProvider dataProtectionProvider,
    IOptions<EmailConfirmationTokenProviderOptions> options,
    ILogger<DataProtectorTokenProvider<AppUser>> logger)
    : DataProtectorTokenProvider<AppUser>(dataProtectionProvider, options, logger)
{
    public const string ProviderName = "EmailConfirmation";
}

internal sealed class EmailConfirmationTokenProviderOptions : DataProtectionTokenProviderOptions
{
    public EmailConfirmationTokenProviderOptions()
    {
        Name = EmailConfirmationTokenProvider.ProviderName;
        TokenLifespan = TimeSpan.FromDays(3);
    }
}
