using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;
using TicketRush.Domain.Users;

namespace TicketRush.Application.Features.Auth;

internal static class RefreshTokenFamilies
{
    /// <summary>Revokes every still-active token of the session family. Caller saves.</summary>
    public static async Task RevokeFamilyAsync(this IAppDbContext db, Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync(ct);
        active.ForEach(t => t.Revoke(now));
    }

    /// <summary>Issues the first token of a new family and tracks it. Caller saves.</summary>
    public static (RefreshToken Token, string RawToken) StartFamily(this IAppDbContext db, Guid userId, DateTimeOffset now, ClientInfo client)
    {
        var raw = RefreshTokenSecret.Generate();
        var token = RefreshToken.Issue(userId, RefreshTokenSecret.Hash(raw), now, client.IpAddress, client.UserAgent);
        db.RefreshTokens.Add(token);
        return (token, raw);
    }
}
