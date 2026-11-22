using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Domain.Primitives;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Persistence;

namespace TicketRush.Infrastructure.Identity;

internal sealed class IdentityService(UserManager<AppUser> users, AppDbContext db, TicketRush.Application.Abstractions.Audit.IAuditLogger audit) : IIdentityService
{
    // Unknown emails are checked against this hash so they cost as much time as a wrong password.
    private static readonly PasswordHasher<AppUser> Hasher = new();
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(new AppUser(), Guid.NewGuid().ToString()));

    private static readonly HashSet<string> DuplicateCodes = ["DuplicateUserName", "DuplicateEmail"];

    public async Task<Result> RegisterAsync(string email, string password, string fullName, CancellationToken ct)
    {
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Result.Success();
        }

        var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = fullName };
        user.MarkRegistered();

        // CreateAsync and AddToRoleAsync each save; one transaction keeps user, role and outbox row together.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var created = await users.CreateAsync(user, password);
        if (!created.Succeeded)
        {
            return created.Errors.Any(e => DuplicateCodes.Contains(e.Code)) ? Result.Success() : WeakPassword(created);
        }

        await users.AddToRoleAsync(user, Roles.Customer);
        await transaction.CommitAsync(ct);
        return Result.Success();
    }

    public async Task<Result> ConfirmEmailAsync(Guid userId, string token, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return AuthErrors.InvalidToken;
        }

        var confirmed = await users.ConfirmEmailAsync(user, token);
        return confirmed.Succeeded ? Result.Success() : AuthErrors.InvalidToken;
    }

    public async Task<Result<UserAccount>> CheckPasswordAsync(string email, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            Hasher.VerifyHashedPassword(new AppUser(), DummyHash.Value, password);
            await audit.LogAsync("auth.login_failed", "user", null, new { email }, ct);
            return AuthErrors.InvalidCredentials;
        }

        if (await users.IsLockedOutAsync(user))
        {
            await audit.LogAsync("auth.locked_out", "user", user.Id.ToString(), new { email }, ct);
            return AuthErrors.LockedOut;
        }

        if (!await users.CheckPasswordAsync(user, password))
        {
            await users.AccessFailedAsync(user);
            var locked = await users.IsLockedOutAsync(user);
            // Spec 16: every failed sign-in, and the one that locks the account, is on the record.
            await audit.LogAsync(locked ? "auth.locked_out" : "auth.login_failed", "user", user.Id.ToString(), new { email }, ct);
            return locked ? AuthErrors.LockedOut : AuthErrors.InvalidCredentials;
        }

        await users.ResetAccessFailedCountAsync(user);
        return await ToAccountAsync(user);
    }

    public async Task RequestPasswordResetAsync(string email, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return;
        }

        user.RequestPasswordReset();
        await db.SaveChangesAsync(ct);
    }

    public async Task<Result<Guid>> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            return AuthErrors.InvalidToken;
        }

        var reset = await users.ResetPasswordAsync(user, token, newPassword);
        if (!reset.Succeeded)
        {
            return reset.Errors.Any(e => e.Code == "InvalidToken") ? AuthErrors.InvalidToken : WeakPassword(reset);
        }

        await users.ResetAccessFailedCountAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        return user.Id;
    }

    public async Task<UserAccount?> FindByIdAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        return user is null ? null : await ToAccountAsync(user);
    }

    public async Task<Result<UserAccount>> UpdateProfileAsync(
        Guid userId, string fullName, string? phoneNumber, string? avatarUrl, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return Error.NotFound("user.not_found", "Account not found.");
        }

        user.FullName = fullName;
        user.PhoneNumber = phoneNumber;
        user.AvatarUrl = avatarUrl;
        await users.UpdateAsync(user);
        return await ToAccountAsync(user);
    }

    private async Task<UserAccount> ToAccountAsync(AppUser user)
    {
        var roles = await users.GetRolesAsync(user);
        return new UserAccount(
            user.Id, user.Email!, user.FullName, user.PhoneNumber, user.AvatarUrl, user.EmailConfirmed, [.. roles.Order(StringComparer.Ordinal)]);
    }

    private static Error WeakPassword(IdentityResult result) =>
        AuthErrors.WeakPassword(new Dictionary<string, string[]>
        {
            ["password"] = [.. result.Errors.Select(e => e.Description)],
        });
}
