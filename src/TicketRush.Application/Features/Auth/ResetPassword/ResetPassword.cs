using FluentValidation;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Features.Auth.ResetPassword;

public sealed record ResetPasswordCommand(string Email, string Token, string NewPassword);

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress();
        RuleFor(c => c.Token).NotEmpty().MaximumLength(2048);
        RuleFor(c => c.NewPassword).StrongPassword(c => c.Email);
    }
}

/// <summary>A new password ends every session: all of the user's refresh tokens are revoked.</summary>
public sealed class ResetPasswordHandler(IIdentityService identity, IAppDbContext db, TimeProvider clock)
{
    public async Task<Result> HandleAsync(ResetPasswordCommand command, CancellationToken ct)
    {
        var reset = await identity.ResetPasswordAsync(command.Email.Trim(), command.Token, command.NewPassword, ct);
        if (reset.IsFailure)
        {
            return reset.Error;
        }

        var now = clock.GetUtcNow();
        var userId = reset.Value;
        var active = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);
        active.ForEach(t => t.Revoke(now));
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }
}
