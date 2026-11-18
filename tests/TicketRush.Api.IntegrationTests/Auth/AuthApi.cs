using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.IntegrationTests.Auth;

/// <summary>A cookie as the server set it: value plus the raw attributes.</summary>
public sealed record SetCookie(string Name, string Value, IReadOnlyList<string> Attributes)
{
    public bool Has(string attribute) => Attributes.Any(a => a.Equals(attribute, StringComparison.OrdinalIgnoreCase));

    public string? Attribute(string name) => Attributes
        .Select(a => a.Split('=', 2))
        .FirstOrDefault(p => p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase) && p.Length == 2)?[1];
}

/// <summary>
/// Calls the auth endpoints with explicit cookies (no cookie jar), so tests can present old tokens on purpose.
/// </summary>
public sealed partial class AuthApi(ApiFactory factory, HttpClient client)
{
    public const string Password = "matkhau123";
    public const string RefreshCookie = "tr_refresh";
    public const string SessionCookie = "tr_session";

    public static HttpClient CreateClient(ApiFactory factory) =>
        factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public Task<HttpResponseMessage> RegisterAsync(string email, string password = Password, string fullName = "Nguyễn An") =>
        client.PostAsJsonAsync("/api/v1/auth/register", new { email, password, fullName, captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }, Ct);

    /// <summary>Sends a passing CAPTCHA token too, so tests that fail sign-in on purpose are not stopped by spec 12's CAPTCHA.</summary>
    public Task<HttpResponseMessage> LoginAsync(string email, string password = Password) =>
        client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, captchaToken = TicketRush.Infrastructure.Security.CaptchaOptions.FakePassToken }, Ct);

    public Task<HttpResponseMessage> RefreshAsync(string? refreshToken, bool csrfHeader = true) =>
        SendWithCookieAsync("/api/v1/auth/refresh", refreshToken, csrfHeader);

    public Task<HttpResponseMessage> LogoutAsync(string? refreshToken, bool csrfHeader = true) =>
        SendWithCookieAsync("/api/v1/auth/logout", refreshToken, csrfHeader);

    public Task<HttpResponseMessage> ForgotPasswordAsync(string email) =>
        client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email }, Ct);

    public Task<HttpResponseMessage> ResetPasswordAsync(string email, string token, string newPassword) =>
        client.PostAsJsonAsync("/api/v1/auth/reset-password", new { email, token, newPassword }, Ct);

    public Task<HttpResponseMessage> ConfirmEmailAsync(Guid userId, string token) =>
        client.PostAsJsonAsync("/api/v1/auth/confirm-email", new { userId, token }, Ct);

    public Task<HttpResponseMessage> GetAsync(string path, string? accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accessToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        return client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, object body, string accessToken)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, Ct);
    }

    /// <summary>Registers, confirms through the emailed link and logs in. Returns the login response.</summary>
    public async Task<LoginResult> SignUpAsync(string email)
    {
        (await RegisterAsync(email)).EnsureSuccessStatusCode();
        await factory.DrainOutboxAsync(Ct);
        var link = LinkSentTo(email);
        (await ConfirmEmailAsync(Guid.Parse(QueryValue(link, "userId")), QueryValue(link, "token"))).EnsureSuccessStatusCode();
        return await LoginResult.ReadAsync(await LoginAsync(email));
    }

    /// <summary>Creates a confirmed user with a role directly (registration only ever grants Customer).</summary>
    public async Task<LoginResult> SignInWithRoleAsync(string email, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { Id = Guid.CreateVersion7(), Email = email, UserName = email, FullName = role, EmailConfirmed = true };
            (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await users.AddToRoleAsync(user, role)).Succeeded.ShouldBeTrue();
        }

        return await LoginResult.ReadAsync(await LoginAsync(email));
    }

    public string LinkSentTo(string email)
    {
        var message = factory.Emails.Sent.Last(m => m.To == email);
        return LinkPattern().Match(message.TextBody).Value;
    }

    public static string QueryValue(string url, string name) =>
        Uri.UnescapeDataString(new Uri(url).Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2))
            .Single(p => p[0] == name)[1]);

    public static SetCookie? Cookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var headers))
        {
            return null;
        }

        return headers
            .Select(header => header.Split(';').Select(part => part.Trim()).ToList())
            .Where(parts => parts[0].StartsWith(name + "=", StringComparison.Ordinal))
            .Select(parts => new SetCookie(name, parts[0][(name.Length + 1)..], parts[1..]))
            .LastOrDefault();
    }

    private Task<HttpResponseMessage> SendWithCookieAsync(string path, string? refreshToken, bool csrfHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (refreshToken is not null)
        {
            request.Headers.Add("Cookie", $"{RefreshCookie}={refreshToken}");
        }

        if (csrfHeader)
        {
            request.Headers.Add("X-Requested-With", "fetch");
        }

        return client.SendAsync(request, Ct);
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex LinkPattern();
}

public sealed record LoginResult(HttpResponseMessage Response, JsonElement Body, string AccessToken, string RefreshToken)
{
    public static async Task<LoginResult> ReadAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.Clone();
        var refresh = AuthApi.Cookie(response, AuthApi.RefreshCookie)?.Value ?? throw new InvalidOperationException("No refresh cookie set.");
        return new LoginResult(response, body, body.GetProperty("accessToken").GetString()!, refresh);
    }
}
