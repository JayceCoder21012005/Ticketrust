namespace TicketRush.Api.Common.Auth;

/// <summary>Endpoints require these policies; handlers never check role strings (spec 03).</summary>
public static class AuthPolicies
{
    public const string RequireOrganizer = nameof(RequireOrganizer);
    public const string RequireAdmin = nameof(RequireAdmin);
    public const string RequireGateStaff = nameof(RequireGateStaff);
}
