using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Auth;

public sealed class AuthFlowTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _api = new AuthApi(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Register_confirm_login_refresh_logout()
    {
        var register = await _api.RegisterAsync("an@example.com");
        register.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await _factory.DrainOutboxAsync(Ct);
        var link = _api.LinkSentTo("an@example.com");
        link.ShouldStartWith("http://localhost:3000/verify-email?");
        var confirm = await _api.ConfirmEmailAsync(Guid.Parse(AuthApi.QueryValue(link, "userId")), AuthApi.QueryValue(link, "token"));
        confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var login = await LoginResult.ReadAsync(await _api.LoginAsync("an@example.com"));
        login.Body.GetProperty("user").GetProperty("email").GetString().ShouldBe("an@example.com");
        login.Body.GetProperty("user").GetProperty("emailConfirmed").GetBoolean().ShouldBeTrue();
        login.Body.GetProperty("user").GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe(["Customer"]);
        login.Body.TryGetProperty("refreshToken", out _).ShouldBeFalse("the refresh token only travels in the HttpOnly cookie");

        var refreshed = await LoginResult.ReadAsync(await _api.RefreshAsync(login.RefreshToken));
        refreshed.RefreshToken.ShouldNotBe(login.RefreshToken);
        refreshed.AccessToken.ShouldNotBe(login.AccessToken);

        var logout = await _api.LogoutAsync(refreshed.RefreshToken);
        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        AuthApi.Cookie(logout, AuthApi.RefreshCookie)!.Value.ShouldBeEmpty();
        AuthApi.Cookie(logout, AuthApi.SessionCookie)!.Value.ShouldBeEmpty();

        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(refreshed.RefreshToken));
    }

    [Fact]
    public async Task Login_sets_a_strict_HttpOnly_refresh_cookie_scoped_to_auth_and_a_session_hint()
    {
        var login = await _api.SignUpAsync("cookie@example.com");

        var refresh = AuthApi.Cookie(login.Response, AuthApi.RefreshCookie)!;
        refresh.Value.Length.ShouldBe(43);
        refresh.Has("httponly").ShouldBeTrue();
        refresh.Has("secure").ShouldBeTrue();
        refresh.Attribute("samesite").ShouldBe("strict", StringCompareShould.IgnoreCase);
        refresh.Attribute("path").ShouldBe("/api/v1/auth");
        refresh.Attribute("expires").ShouldNotBeNull();

        var hint = AuthApi.Cookie(login.Response, AuthApi.SessionCookie)!;
        hint.Value.ShouldBe("1");
        hint.Attribute("path").ShouldBe("/");
        hint.Attribute("samesite").ShouldBe("lax", StringCompareShould.IgnoreCase);
        hint.Attribute("expires").ShouldBe(refresh.Attribute("expires"));
    }

    [Fact]
    public async Task Access_token_carries_sub_email_role_and_jti_and_expires_in_10_minutes()
    {
        var login = await _api.SignUpAsync("claims@example.com");

        var payload = JwtPayload(login.AccessToken);
        payload.GetProperty("sub").GetString().ShouldBe(login.Body.GetProperty("user").GetProperty("id").GetString());
        payload.GetProperty("email").GetString().ShouldBe("claims@example.com");
        payload.GetProperty("role").ToString().ShouldContain("Customer");
        payload.GetProperty("jti").GetString().ShouldNotBeNullOrEmpty();
        (payload.GetProperty("exp").GetInt64() - payload.GetProperty("iat").GetInt64()).ShouldBe(600);
        login.Body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(DateTimeOffset.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Reusing_a_rotated_refresh_token_revokes_the_whole_family()
    {
        var login = await _api.SignUpAsync("reuse@example.com");
        var rotated = await LoginResult.ReadAsync(await _api.RefreshAsync(login.RefreshToken));

        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(login.RefreshToken));

        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(rotated.RefreshToken));
    }

    [Fact]
    public async Task Reuse_detection_leaves_other_sessions_of_the_user_alone()
    {
        var phone = await _api.SignUpAsync("two-devices@example.com");
        var laptop = await LoginResult.ReadAsync(await _api.LoginAsync("two-devices@example.com"));
        await _api.RefreshAsync(phone.RefreshToken);

        await _api.RefreshAsync(phone.RefreshToken);

        (await _api.RefreshAsync(laptop.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_and_logout_require_the_X_Requested_With_header()
    {
        var login = await _api.SignUpAsync("csrf@example.com");

        foreach (var response in new[] { await _api.RefreshAsync(login.RefreshToken, csrfHeader: false), await _api.LogoutAsync(login.RefreshToken, csrfHeader: false) })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await CodeOf(response)).ShouldBe("auth.csrf_header_missing");
        }

        (await _api.RefreshAsync(login.RefreshToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_without_a_cookie_is_refresh_invalid()
    {
        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(null));
        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync("not-a-real-token"));
    }

    [Fact]
    public async Task Wrong_email_and_wrong_password_return_identical_bodies_and_similar_timing()
    {
        await _api.SignUpAsync("timing@example.com");
        var wrongPassword = new List<double>();
        var unknownEmail = new List<double>();
        string? wrongPasswordBody = null;
        string? unknownEmailBody = null;

        for (var i = 0; i < 5; i++)
        {
            (wrongPasswordBody, var elapsed) = await TimedLoginAsync("timing@example.com", "wrongpass123");
            wrongPassword.Add(elapsed);
            (unknownEmailBody, elapsed) = await TimedLoginAsync("nobody@example.com", "wrongpass123");
            unknownEmail.Add(elapsed);
            await _api.LoginAsync("timing@example.com"); // resets the failure count so lockout doesn't kick in
        }

        Strip(unknownEmailBody!).ShouldBe(Strip(wrongPasswordBody!));
        (await CodeOf(await _api.LoginAsync("nobody@example.com", "x"))).ShouldBe("auth.invalid_credentials");
        var ratio = Median(unknownEmail) / Median(wrongPassword);
        ratio.ShouldBeInRange(0.5, 2.0, $"unknown email {Median(unknownEmail):F1} ms vs wrong password {Median(wrongPassword):F1} ms");
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_even_for_the_right_password()
    {
        await _api.SignUpAsync("lock@example.com");

        for (var i = 0; i < 4; i++)
        {
            (await CodeOf(await _api.LoginAsync("lock@example.com", "wrongpass123"))).ShouldBe("auth.invalid_credentials");
        }

        (await CodeOf(await _api.LoginAsync("lock@example.com", "wrongpass123"))).ShouldBe("auth.locked_out");
        var correct = await _api.LoginAsync("lock@example.com");
        correct.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodeOf(correct)).ShouldBe("auth.locked_out");
    }

    [Fact]
    public async Task Unconfirmed_users_can_log_in()
    {
        (await _api.RegisterAsync("unconfirmed@example.com")).EnsureSuccessStatusCode();

        var login = await LoginResult.ReadAsync(await _api.LoginAsync("unconfirmed@example.com"));

        login.Body.GetProperty("user").GetProperty("emailConfirmed").GetBoolean().ShouldBeFalse();
        JwtPayload(login.AccessToken).GetProperty("email_verified").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Register_rejects_a_weak_password_with_auth_weak_password()
    {
        var response = await _api.RegisterAsync("weak@example.com", "onlyletters");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("auth.weak_password");
    }

    [Fact]
    public async Task Registering_an_existing_email_looks_the_same_and_sends_nothing()
    {
        await _api.SignUpAsync("taken@example.com");
        var emailsBefore = _factory.Emails.Sent.Count;

        var again = await _api.RegisterAsync("taken@example.com", "anotherpass9", "Someone Else");
        await _factory.DrainOutboxAsync(Ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Emails.Sent.Count.ShouldBe(emailsBefore);
        (await _api.LoginAsync("taken@example.com", "anotherpass9")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Confirm_email_rejects_a_forged_token()
    {
        (await _api.RegisterAsync("forged@example.com")).EnsureSuccessStatusCode();
        await _factory.DrainOutboxAsync(Ct);
        var userId = Guid.Parse(AuthApi.QueryValue(_api.LinkSentTo("forged@example.com"), "userId"));

        var response = await _api.ConfirmEmailAsync(userId, "forged-token");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(response)).ShouldBe("auth.invalid_token");
    }

    [Fact]
    public async Task Forgot_password_returns_202_for_an_unknown_email_and_sends_nothing()
    {
        var response = await _api.ForgotPasswordAsync("ghost@example.com");
        await _factory.DrainOutboxAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        _factory.Emails.Sent.ShouldNotContain(m => m.To == "ghost@example.com");
    }

    [Fact]
    public async Task Reset_password_with_the_emailed_token_changes_the_password_and_ends_every_session()
    {
        var first = await _api.SignUpAsync("reset@example.com");
        var second = await LoginResult.ReadAsync(await _api.LoginAsync("reset@example.com"));

        (await _api.ForgotPasswordAsync("reset@example.com")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await _factory.DrainOutboxAsync(Ct);
        var link = _api.LinkSentTo("reset@example.com");
        link.ShouldStartWith("http://localhost:3000/reset-password?");
        var reset = await _api.ResetPasswordAsync("reset@example.com", AuthApi.QueryValue(link, "token"), "matkhaumoi456");

        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(first.RefreshToken));
        await ShouldBeRefreshInvalidAsync(await _api.RefreshAsync(second.RefreshToken));
        (await _api.LoginAsync("reset@example.com")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _api.LoginAsync("reset@example.com", "matkhaumoi456")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var reused = await _api.ResetPasswordAsync("reset@example.com", AuthApi.QueryValue(link, "token"), "matkhaukhac789");
        (await CodeOf(reused)).ShouldBe("auth.invalid_token");
    }

    private async Task<(string Body, double ElapsedMs)> TimedLoginAsync(string email, string password)
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await _api.LoginAsync(email, password);
        var body = await response.Content.ReadAsStringAsync(Ct);
        return (body, stopwatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>traceId and correlationId differ per request by design; everything else must match.</summary>
    private static string Strip(string problem)
    {
        using var json = JsonDocument.Parse(problem);
        return string.Join('|', json.RootElement.EnumerateObject().Where(p => p.Name is not ("traceId" or "correlationId")).Select(p => $"{p.Name}={p.Value}"));
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);

    private static async Task ShouldBeRefreshInvalidAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CodeOf(response)).ShouldBe("auth.refresh_invalid");
    }

    private static async Task<string?> CodeOf(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private static JsonElement JwtPayload(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
    }
}
