using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;

namespace TicketRush.Application.Features.Auth.Refresh;

/// <summary>
/// Rotates the refresh token. A token that was already rotated or revoked being presented again means it
/// leaked: the whole family is revoked, so the thief and the victim both have to sign in again.
/// </summary>
public sealed partial class RefreshHandler(
    IAppDbContext db,
    IIdentityService identity,
    IAccessTokenIssuer issuer,
    Abstractions.Audit.IAuditLogger audit,
    TimeProvider clock,
    ILogger<RefreshHandler> logger)
{
    public async Task<Result<AuthSession>> HandleAsync(string? rawToken, ClientInfo client, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return AuthErrors.RefreshInvalid;
        }

        var hash = RefreshTokenSecret.Hash(rawToken);
        var current = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();

        if (current is { IsRevoked: true })
        {
            await RevokeStolenFamilyAsync(current, now, client, ct);
            return AuthErrors.RefreshInvalid;
        }

        if (current is null || !current.IsActive(now))
        {
            return AuthErrors.RefreshInvalid;
        }

        var user = await identity.FindByIdAsync(current.UserId, ct);
        return user is null ? AuthErrors.RefreshInvalid : await RotateAsync(current, user, now, client, ct);
    }

    private async Task<Result<AuthSession>> RotateAsync(
        RefreshToken current, UserAccount user, DateTimeOffset now, ClientInfo client, CancellationToken ct)
    {
        var raw = RefreshTokenSecret.Generate();
        var next = current.Rotate(RefreshTokenSecret.Hash(raw), now, client.IpAddress, client.UserAgent);
        db.RefreshTokens.Add(next);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A parallel refresh with the same token won (xmin); this one loses without punishing the family.
            return AuthErrors.RefreshInvalid;
        }

        return new AuthSession(issuer.Issue(user), raw, next.ExpiresAt, user);
    }

    private async Task RevokeStolenFamilyAsync(RefreshToken reused, DateTimeOffset now, ClientInfo client, CancellationToken ct)
    {
        await db.RevokeFamilyAsync(reused.FamilyId, now, ct);
        audit.Record("auth.refresh_reuse", "user", reused.UserId.ToString(), new { familyId = reused.FamilyId, ip = client.IpAddress });
        await db.SaveChangesAsync(ct);
        LogTokenReuse(reused.UserId, reused.FamilyId, client.IpAddress);
    }

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning,
        Message = "Security: refresh token reuse detected for user {UserId}; revoked family {FamilyId} (request from {IpAddress})")]
    private partial void LogTokenReuse(Guid userId, Guid familyId, string? ipAddress);
}
