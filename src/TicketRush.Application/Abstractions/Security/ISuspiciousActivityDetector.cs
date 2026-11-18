namespace TicketRush.Application.Abstractions.Security;

/// <summary>
/// Spec 12 velocity flags. Flags accounts for admin review and never blocks: every method is best effort and never throws, so a
/// detector outage cannot fail a join or a profile update.
/// </summary>
public interface ISuspiciousActivityDetector
{
    /// <summary>After a successful queue join: more than 20 joins in one minute from the caller's /24 (IPv4) or /48 (IPv6) flags the account.</summary>
    Task QueueJoinedAsync(Guid userId, string? ipAddress, CancellationToken ct);

    /// <summary>After a phone number is saved: a number on more than 3 accounts flags every account that has it.</summary>
    Task PhoneNumberSavedAsync(Guid userId, string? phoneNumber, CancellationToken ct);
}
