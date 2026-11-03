using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Persistence;

namespace TicketRush.Application.Features.Auth.Logout;

/// <summary>Ends the session family of the presented token. Unknown tokens are ignored: logout always succeeds.</summary>
public sealed class LogoutHandler(IAppDbContext db, TimeProvider clock)
{
    public async Task HandleAsync(string? rawToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return;
        }

        var hash = RefreshTokenSecret.Hash(rawToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null)
        {
            return;
        }

        await db.RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);
    }
}
