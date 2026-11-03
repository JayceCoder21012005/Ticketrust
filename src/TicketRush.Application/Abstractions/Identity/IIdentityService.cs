using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Identity;

public sealed record UserAccount(
    Guid Id,
    string Email,
    string FullName,
    string? PhoneNumber,
    string? AvatarUrl,
    bool EmailConfirmed,
    IReadOnlyList<string> Roles);

/// <summary>
/// Port over ASP.NET Core Identity (Infrastructure), which Application must not reference.
/// Implementations never reveal whether an email is registered.
/// </summary>
public interface IIdentityService
{
    /// <summary>
    /// Creates a Customer and queues the confirmation email. An email that is already registered
    /// also succeeds, without changes, so the response can't be used to enumerate accounts.
    /// </summary>
    Task<Result> RegisterAsync(string email, string password, string fullName, CancellationToken ct);

    Task<Result> ConfirmEmailAsync(Guid userId, string token, CancellationToken ct);

    /// <summary>Checks the password with lockout. Unknown emails cost one hash too, so timing matches.</summary>
    Task<Result<UserAccount>> CheckPasswordAsync(string email, string password, CancellationToken ct);

    /// <summary>Queues the reset email when the email is registered; does nothing otherwise.</summary>
    Task RequestPasswordResetAsync(string email, CancellationToken ct);

    /// <returns>The id of the user whose password changed.</returns>
    Task<Result<Guid>> ResetPasswordAsync(string email, string token, string newPassword, CancellationToken ct);

    Task<UserAccount?> FindByIdAsync(Guid userId, CancellationToken ct);

    Task<Result<UserAccount>> UpdateProfileAsync(
        Guid userId,
        string fullName,
        string? phoneNumber,
        string? avatarUrl,
        CancellationToken ct);
}
