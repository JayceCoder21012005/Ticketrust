using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Queue;

namespace TicketRush.Api.IntegrationTests.Queue;

public sealed class AdmissionTokenFactoryTests
{
    private const string AdmissionKey = "admission-key-0123456789-0123456789-abcdef";
    private const string AccessKey = "test-signing-key-that-is-long-enough-0123456789";

    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Event = Guid.NewGuid();
    private static readonly Guid Phase = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero));

    private AdmissionTokenFactory Factory(string key = AdmissionKey) =>
        new(Options.Create(new AdmissionTokenOptions { SigningKey = key }), _time);

    private static string Forge(string key, string audience, string issuer, DateTimeOffset expires, string algorithm = SecurityAlgorithms.HmacSha256) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Expires = expires.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = User.ToString(),
                ["evt"] = Event.ToString(),
                ["phase"] = Phase.ToString(),
                ["jti"] = Guid.NewGuid().ToString(),
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), algorithm),
        });

    [Fact]
    public void Round_trip_returns_the_claims()
    {
        var expires = _time.GetUtcNow().AddMinutes(10);

        var token = Factory().Create(User, Event, Phase, expires);
        var claims = Factory().Validate(token);

        claims.ShouldNotBeNull();
        claims.UserId.ShouldBe(User);
        claims.EventId.ShouldBe(Event);
        claims.PhaseId.ShouldBe(Phase);
        claims.Jti.ShouldNotBeNullOrEmpty();
        claims.ExpiresAt.ShouldBe(expires, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Tampered_payload_is_rejected()
    {
        var token = Factory().Create(User, Event, Phase, _time.GetUtcNow().AddMinutes(10));
        var parts = token.Split('.');
        var otherPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"sub":"{{Guid.NewGuid()}}"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Factory().Validate($"{parts[0]}.{otherPayload}.{parts[2]}").ShouldBeNull();
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var token = Factory().Create(User, Event, Phase, _time.GetUtcNow().AddMinutes(10));

        _time.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));

        Factory().Validate(token).ShouldBeNull();
    }

    [Fact]
    public void Token_signed_with_the_access_token_key_is_rejected()
    {
        var forged = Forge(AccessKey, "ticketrush-admission", "ticketrush-api", _time.GetUtcNow().AddMinutes(10));

        Factory().Validate(forged).ShouldBeNull();
    }

    [Fact]
    public void Same_key_with_wrong_audience_or_issuer_is_rejected()
    {
        var expires = _time.GetUtcNow().AddMinutes(10);

        Factory().Validate(Forge(AdmissionKey, "ticketrush-web", "ticketrush-api", expires)).ShouldBeNull();
        Factory().Validate(Forge(AdmissionKey, "ticketrush-admission", "someone-else", expires)).ShouldBeNull();
    }

    [Fact]
    public void Unsigned_alg_none_token_is_rejected()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = _time.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
        var none = $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"iss":"ticketrush-api","aud":"ticketrush-admission","sub":"{{User}}","evt":"{{Event}}","phase":"{{Phase}}","jti":"x","exp":{{exp}}}""")}.";

        Factory().Validate(none).ShouldBeNull();
    }

    [Fact]
    public void Garbage_is_rejected_without_throwing()
    {
        Factory().Validate("").ShouldBeNull();
        Factory().Validate("not-a-jwt").ShouldBeNull();
        Factory().Validate("a.b.c").ShouldBeNull();
    }

    [Fact]
    public void Token_missing_a_required_claim_is_rejected()
    {
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "ticketrush-api",
            Audience = "ticketrush-admission",
            Expires = _time.GetUtcNow().AddMinutes(10).UtcDateTime,
            Claims = new Dictionary<string, object> { ["sub"] = User.ToString() },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AdmissionKey)), SecurityAlgorithms.HmacSha256),
        });

        Factory().Validate(token).ShouldBeNull();
    }
}
