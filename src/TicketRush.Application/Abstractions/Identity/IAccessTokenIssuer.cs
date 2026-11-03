namespace TicketRush.Application.Abstractions.Identity;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Issues the short-lived JWT (claims <c>sub</c>, <c>email</c>, <c>role</c>, <c>jti</c>).</summary>
public interface IAccessTokenIssuer
{
    AccessToken Issue(UserAccount user);
}
