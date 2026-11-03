using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Api.Common.Auth;

public static class AuthenticationSetup
{
    public static IServiceCollection AddApiAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
            {
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Value.Issuer,
                    ValidAudience = jwt.Value.Audience,
                    IssuerSigningKey = AuthClaims.SigningKey(jwt.Value),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = AuthClaims.Name,
                    RoleClaimType = AuthClaims.Role,
                };
                bearer.Events = new JwtBearerEvents { OnMessageReceived = ReadHubQueryToken };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.RequireOrganizer, policy => policy.RequireRole(Roles.Organizer))
            .AddPolicy(AuthPolicies.RequireAdmin, policy => policy.RequireRole(Roles.Admin))
            .AddPolicy(AuthPolicies.RequireGateStaff, policy => policy.RequireRole(Roles.GateStaff));
        return services;
    }

    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(AuthClaims.Subject) ?? throw new InvalidOperationException("Authenticated user has no sub claim."));

    /// <summary>
    /// Browsers can't set headers on WebSocket/SSE requests, so SignalR sends the token as <c>access_token</c>.
    /// Only accepted on <c>/hubs/*</c>: query strings end up in logs and history everywhere else.
    /// </summary>
    private static Task ReadHubQueryToken(MessageReceivedContext context)
    {
        var request = context.Request;
        var token = request.Query["access_token"].ToString();
        if (!string.IsNullOrEmpty(token) && request.PathBase.Add(request.Path).StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase))
        {
            context.Token = token;
        }

        return Task.CompletedTask;
    }
}
