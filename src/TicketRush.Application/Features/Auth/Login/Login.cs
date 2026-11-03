using FluentValidation;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Auth.Login;

public sealed record LoginCommand(string Email, string Password);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(c => c.Email).NotEmpty().MaximumLength(256);
        RuleFor(c => c.Password).NotEmpty().MaximumLength(256);
    }
}

/// <summary>Unconfirmed emails may sign in; buying and queueing check confirmation (spec 03 rules).</summary>
public sealed class LoginHandler(IIdentityService identity, IAccessTokenIssuer issuer, IAppDbContext db, TimeProvider clock)
{
    public async Task<Result<AuthSession>> HandleAsync(LoginCommand command, ClientInfo client, CancellationToken ct)
    {
        var check = await identity.CheckPasswordAsync(command.Email.Trim(), command.Password, ct);
        if (check.IsFailure)
        {
            return check.Error;
        }

        var user = check.Value;
        var (refreshToken, raw) = db.StartFamily(user.Id, clock.GetUtcNow(), client);
        await db.SaveChangesAsync(ct);
        return new AuthSession(issuer.Issue(user), raw, refreshToken.ExpiresAt, user);
    }
}
