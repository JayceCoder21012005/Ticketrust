namespace TicketRush.Application.Abstractions.Queue;

public sealed record AdmissionTokenClaims(Guid UserId, Guid EventId, Guid PhaseId, string Jti, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates and validates admission tokens. They are signed with their own key, so an access token can never
/// pass as an admission token (spec 06, C7).
/// </summary>
public interface IAdmissionTokenFactory
{
    string Create(Guid userId, Guid eventId, Guid phaseId, DateTimeOffset expiresAt);

    /// <returns>The claims, or <c>null</c> for a bad signature, wrong algorithm, wrong issuer/audience or an expired token.</returns>
    AdmissionTokenClaims? Validate(string token);
}
