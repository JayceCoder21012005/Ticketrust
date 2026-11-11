using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Notifications;

internal sealed class UserDirectory(AppDbContext db) : IUserDirectory
{
    public async Task<UserContact?> GetContactAsync(Guid userId, CancellationToken ct) =>
        await db.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserContact(u.Email ?? string.Empty, u.FullName, u.EmailConfirmed))
            .SingleOrDefaultAsync(ct);
}
