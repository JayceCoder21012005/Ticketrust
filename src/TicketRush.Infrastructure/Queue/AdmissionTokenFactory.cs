using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicketRush.Application.Abstractions.Queue;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Queue;

/// <summary>
/// Creates and validates admission tokens (spec 06). HS256 only, signed with <c>AdmissionToken:SigningKey</c>, which is
/// never the access-token key, and bound to user + event + phase so a shared token is useless to anyone else (C7).
/// </summary>
public sealed class AdmissionTokenFactory(IOptions<AdmissionTokenOptions> options, TimeProvider time) : IAdmissionTokenFactory
{
    private const string EventClaim = "evt";
    private const string PhaseClaim = "phase";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(options.Value.SigningKey));

    public string Create(Guid userId, Guid eventId, Guid phaseId, DateTimeOffset expiresAt)
    {
        var now = time.GetUtcNow();
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Value.Issuer,
            Audience = options.Value.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = userId.ToString(),
                [EventClaim] = eventId.ToString(),
                [PhaseClaim] = phaseId.ToString(),
                [JwtRegisteredClaimNames.Jti] = Guid.CreateVersion7().ToString(),
            },
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });
    }

    public AdmissionTokenClaims? Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var result = _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = options.Value.Issuer,
            ValidAudience = options.Value.Audience,
            IssuerSigningKey = _key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            // Lifetime is checked below against the injected clock, so it stays testable and exact (no skew).
            ValidateLifetime = false,
        }).GetAwaiter().GetResult();

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        var expiresAt = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        if (expiresAt <= time.GetUtcNow())
        {
            return null;
        }

        return Guid.TryParse(Claim(jwt, JwtRegisteredClaimNames.Sub), out var userId)
            && Guid.TryParse(Claim(jwt, EventClaim), out var eventId)
            && Guid.TryParse(Claim(jwt, PhaseClaim), out var phaseId)
            && Claim(jwt, JwtRegisteredClaimNames.Jti) is { Length: > 0 } jti
            ? new AdmissionTokenClaims(userId, eventId, phaseId, jti, expiresAt)
            : null;
    }

    private static string? Claim(JsonWebToken jwt, string type) =>
        jwt.TryGetPayloadValue<string>(type, out var value) ? value : null;
}
