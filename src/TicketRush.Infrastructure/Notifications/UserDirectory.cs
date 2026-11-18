using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Users;
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

    public async Task<Guid?> FindGateStaffIdAsync(string email, CancellationToken ct)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await (
            from u in db.Users.AsNoTracking()
            where u.NormalizedEmail == normalized
            join ur in db.UserRoles on u.Id equals ur.UserId
            join r in db.Roles on ur.RoleId equals r.Id
            where r.Name == Roles.GateStaff
            select (Guid?)u.Id).FirstOrDefaultAsync(ct);
    }
}
