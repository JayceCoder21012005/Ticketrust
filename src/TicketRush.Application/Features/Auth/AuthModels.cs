using TicketRush.Application.Abstractions.Identity;

namespace TicketRush.Application.Features.Auth;

/// <summary>Who is calling; stored on refresh tokens so users can see where sessions come from.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent);

/// <summary>Result of login and refresh. The API puts <see cref="RefreshToken"/> in a cookie, never in the body.</summary>
public sealed record AuthSession(
    AccessToken AccessToken,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserAccount User);
