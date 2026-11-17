namespace TicketRush.Application.Abstractions.Notifications;

public sealed record UserContact(string Email, string FullName, bool EmailConfirmed);

/// <summary>Reads how to reach a user, without exposing the Identity types to the use cases.</summary>
public interface IUserDirectory
{
    /// <returns><c>null</c> when the user does not exist.</returns>
    Task<UserContact?> GetContactAsync(Guid userId, CancellationToken ct);

    /// <returns>The id of the account with this email when it has the <c>GateStaff</c> role; <c>null</c> otherwise (unknown email or another role).</returns>
    Task<Guid?> FindGateStaffIdAsync(string email, CancellationToken ct);
}
