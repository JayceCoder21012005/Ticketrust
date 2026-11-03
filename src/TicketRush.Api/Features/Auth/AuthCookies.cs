using TicketRush.Application.Features.Auth;

namespace TicketRush.Api.Features.Auth;

/// <summary>
/// <c>tr_refresh</c>: the refresh token, HttpOnly + Strict, only sent to <c>/api/v1/auth</c>.
/// <c>tr_session</c>: a non-secret "probably signed in" hint for page routes (<c>proxy.ts</c>) and for the
/// web app to skip the refresh call for anonymous visitors. It grants nothing on its own.
/// </summary>
public static class AuthCookies
{
    public const string Refresh = "tr_refresh";
    public const string Session = "tr_session";
    public const string RefreshPath = "/api/v1/auth";

    public static void Append(HttpResponse response, AuthSession session)
    {
        response.Cookies.Append(Refresh, session.RefreshToken, RefreshOptions(session.RefreshTokenExpiresAt));
        response.Cookies.Append(Session, "1", SessionOptions(session.RefreshTokenExpiresAt));
    }

    public static void Clear(HttpResponse response)
    {
        response.Cookies.Delete(Refresh, RefreshOptions(expires: null));
        response.Cookies.Delete(Session, SessionOptions(expires: null));
    }

    private static CookieOptions RefreshOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = RefreshPath,
        Expires = expires,
        IsEssential = true,
    };

    private static CookieOptions SessionOptions(DateTimeOffset? expires) => new()
    {
        HttpOnly = false,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };
}
