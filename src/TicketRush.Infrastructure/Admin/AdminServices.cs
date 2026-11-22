using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Admin;
using TicketRush.Application.Features.Admin;
using TicketRush.Domain.Primitives;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Admin;

/// <summary>The admin's view of Identity users (spec 16). Changes go through <see cref="UserManager{TUser}"/>, which saves the context.</summary>
public sealed class AdminUsers(AppDbContext db, UserManager<AppUser> users, TimeProvider time) : IAdminUsers
{
    public async Task<AdminPage<AdminUser>> SearchAsync(string? query, string? cursor, int limit, CancellationToken ct)
    {
        AdminCursor.TryDecode(cursor, out var after);
        var rows = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim();
            rows = Guid.TryParse(q, out var id)
                ? rows.Where(u => u.Id == id)
                : rows.Where(u => EF.Functions.ILike(u.Email!, $"%{q}%") || EF.Functions.ILike(u.FullName, $"%{q}%") || u.PhoneNumber == q);
        }

        if (after is { } c)
        {
            rows = rows.Where(u => u.CreatedAt < c.At || (u.CreatedAt == c.At && u.Id.CompareTo(c.Id) < 0));
        }

        var page = await rows.OrderByDescending(u => u.CreatedAt).ThenByDescending(u => u.Id).Take(limit + 1).ToListAsync(ct);
        var roles = await RolesOfAsync([.. page.Select(u => u.Id)], ct);
        var items = page.Take(limit).Select(u => ToModel(u, roles.GetValueOrDefault(u.Id, []))).ToList();
        return new AdminPage<AdminUser>(items, page.Count > limit ? AdminCursor.Encode(page[limit - 1].CreatedAt, page[limit - 1].Id) : null);
    }

    public async Task<AdminUser?> GetAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is null ? null : ToModel(user, (await RolesOfAsync([userId], ct)).GetValueOrDefault(userId, []));
    }

    public Task<Result> LockAsync(Guid userId, CancellationToken ct) => ChangeAsync(userId, async user =>
    {
        var now = time.GetUtcNow();
        (await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null).ToListAsync(ct)).ForEach(t => t.Revoke(now));
        await users.SetLockoutEnabledAsync(user, true);
        return await users.SetLockoutEndDateAsync(user, DateTimeOffset.MaxValue);
    });

    public Task<Result> UnlockAsync(Guid userId, CancellationToken ct) => ChangeAsync(userId, async user =>
    {
        var result = await users.SetLockoutEndDateAsync(user, null);
        return result.Succeeded ? await users.ResetAccessFailedCountAsync(user) : result;
    });

    public Task<Result> GrantRoleAsync(Guid userId, string role, CancellationToken ct) =>
        ChangeAsync(userId, async user => await users.IsInRoleAsync(user, role) ? await SaveAsync() : await users.AddToRoleAsync(user, role));

    public Task<Result> RevokeRoleAsync(Guid userId, string role, CancellationToken ct) =>
        ChangeAsync(userId, async user => await users.IsInRoleAsync(user, role) ? await users.RemoveFromRoleAsync(user, role) : await SaveAsync());

    /// <summary>Nothing to change in Identity: still commit what the caller recorded (the audit entry).</summary>
    private async Task<IdentityResult> SaveAsync()
    {
        await db.SaveChangesAsync();
        return IdentityResult.Success;
    }

    private async Task<Result> ChangeAsync(Guid userId, Func<AppUser, Task<IdentityResult>> change)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AdminErrors.UserNotFound;
        }

        var result = await change(user);
        return result.Succeeded ? Result.Success() : AdminErrors.WrongState;
    }

    private async Task<Dictionary<Guid, List<string>>> RolesOfAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct) =>
        (await (from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where userIds.Contains(ur.UserId)
                select new { ur.UserId, r.Name }).ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name!).Order().ToList());

    private AdminUser ToModel(AppUser user, IReadOnlyList<string> roles) =>
        new(user.Id, user.Email ?? string.Empty, user.FullName, user.PhoneNumber, user.EmailConfirmed,
            user.LockoutEnd is { } end && end > time.GetUtcNow(), user.LockoutEnd, roles, user.CreatedAt);
}

/// <summary>The outbox's dead letters (spec 16): newest failure first.</summary>
public sealed class AdminOutbox(AppDbContext db, TimeProvider time) : IAdminOutbox
{
    public async Task<AdminPage<DeadLetter>> ListAsync(string? cursor, int limit, CancellationToken ct)
    {
        AdminCursor.TryDecode(cursor, out var after);
        var rows = db.OutboxMessages.AsNoTracking().Where(m => m.ProcessedAt != null && m.LastError != null && m.DiscardedAt == null);
        if (after is { } c)
        {
            rows = rows.Where(m => m.OccurredAt < c.At || (m.OccurredAt == c.At && m.Id.CompareTo(c.Id) < 0));
        }

        var page = await rows.OrderByDescending(m => m.OccurredAt).ThenByDescending(m => m.Id).Take(limit + 1).ToListAsync(ct);
        return new AdminPage<DeadLetter>(
            [.. page.Take(limit).Select(m => new DeadLetter(m.Id, m.Type, m.AggregateId, m.OccurredAt, m.Attempts, m.LastError!, m.Payload))],
            page.Count > limit ? AdminCursor.Encode(page[limit - 1].OccurredAt, page[limit - 1].Id) : null);
    }

    public Task<bool> RetryAsync(Guid messageId, Action record, CancellationToken ct) =>
        ChangeAsync(messageId, record, m => m.Retry(time.GetUtcNow()), ct);

    public Task<bool> DiscardAsync(Guid messageId, string reason, Action record, CancellationToken ct) =>
        ChangeAsync(messageId, record, m => m.Discard(reason, time.GetUtcNow()), ct);

    private async Task<bool> ChangeAsync(Guid messageId, Action record, Action<Outbox.OutboxMessage> change, CancellationToken ct)
    {
        var message = await db.OutboxMessages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is not { IsDeadLettered: true })
        {
            return false;
        }

        change(message);
        record();
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
