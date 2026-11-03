using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicketRush.Application.Abstractions.Identity;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Identity;

/// <summary>Claim names shared by the issuer and the API's bearer validation.</summary>
public static class AuthClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string Email = JwtRegisteredClaimNames.Email;
    public const string Role = "role";
    public const string EmailVerified = "email_verified";
    public const string Name = "name";

    /// <summary>HS256 key from <c>Jwt:SigningKey</c> (validated to be at least 32 characters).</summary>
    public static SymmetricSecurityKey SigningKey(JwtOptions jwt) => new(Encoding.UTF8.GetBytes(jwt.SigningKey));
}

internal sealed class JwtAccessTokenIssuer(IOptions<JwtOptions> options, TimeProvider clock) : IAccessTokenIssuer
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Issue(UserAccount user)
    {
        var jwt = options.Value;
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(jwt.AccessTokenMinutes);

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [AuthClaims.Subject] = user.Id.ToString(),
                [AuthClaims.Email] = user.Email,
                [AuthClaims.Name] = user.FullName,
                [AuthClaims.Role] = user.Roles.ToArray(),
                [AuthClaims.EmailVerified] = user.EmailConfirmed,
                [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
            },
            SigningCredentials = new SigningCredentials(AuthClaims.SigningKey(jwt), SecurityAlgorithms.HmacSha256),
        });

        return new AccessToken(token, expiresAt);
    }
}
