using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Infrastructure.Identity;
using TicketRush.Infrastructure.Security;

namespace TicketRush.Api.IntegrationTests.Auth;

/// <summary>Spec 12 on the account endpoints: Turnstile on register and on sign-in after 3 failures, and the register honeypot.</summary>
public sealed class CaptchaAndHoneypotTests(ContainersFixture containers) : IAsyncLifetime
{
    private const string Pass = CaptchaOptions.FakePassToken;

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(
            await containers.CreateDatabaseAsync(),
            containers.RedisConnectionString,
            settings: new Dictionary<string, string?> { ["Redis:Environment"] = $"cp{Guid.NewGuid():N}"[..12] });
        _client = AuthApi.CreateClient(_factory);
        _api = new AuthApi(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Register_without_a_passing_captcha_creates_nothing()
    {
        var missing = await _client.PostAsJsonAsync("/api/v1/auth/register", new { email = "bot@example.com", password = AuthApi.Password, fullName = "Bot" }, Ct);
        var forged = await _client.PostAsJsonAsync(
            "/api/v1/auth/register", new { email = "bot@example.com", password = AuthApi.Password, fullName = "Bot", captchaToken = "forged" }, Ct);

        (await CodeAsync(missing, HttpStatusCode.BadRequest)).ShouldBe("captcha.failed");
        (await CodeAsync(forged, HttpStatusCode.BadRequest)).ShouldBe("captcha.failed");
        (await UserExistsAsync("bot@example.com")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_filled_honeypot_gets_the_generic_success_and_creates_nothing()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/auth/register",
            new { email = "honey@example.com", password = AuthApi.Password, fullName = "Bot", captchaToken = Pass, website = "http://spam.example" },
            Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted, "the same answer a person gets");
        (await UserExistsAsync("honey@example.com")).ShouldBeFalse();
        await _factory.DrainOutboxAsync(Ct);
        _factory.Emails.Sent.ShouldNotContain(m => m.To == "honey@example.com");
    }

    [Fact]
    public async Task After_three_failed_sign_ins_the_next_needs_a_captcha_and_success_clears_the_count()
    {
        await _api.SignUpAsync("an@example.com");
        for (var i = 0; i < 3; i++)
        {
            (await LoginWithoutCaptchaAsync("an@example.com", "Wrong-password-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"failure {i + 1}");
        }

        (await CodeAsync(await LoginWithoutCaptchaAsync("an@example.com", AuthApi.Password), HttpStatusCode.BadRequest)).ShouldBe("captcha.required");
        (await CodeAsync(await LoginWithCaptchaAsync("an@example.com", AuthApi.Password, "forged"), HttpStatusCode.BadRequest)).ShouldBe("captcha.failed");
        (await LoginWithCaptchaAsync("an@example.com", AuthApi.Password, Pass)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await LoginWithoutCaptchaAsync("an@example.com", AuthApi.Password)).StatusCode.ShouldBe(HttpStatusCode.OK, "a success resets the count");
    }

    [Fact]
    public async Task An_unknown_email_needs_a_captcha_after_three_failures_too_so_the_answer_reveals_no_account()
    {
        for (var i = 0; i < 3; i++)
        {
            (await LoginWithoutCaptchaAsync("nobody@example.com", "Wrong-password-1")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        (await CodeAsync(await LoginWithoutCaptchaAsync("nobody@example.com", "Wrong-password-1"), HttpStatusCode.BadRequest)).ShouldBe("captcha.required");
    }

    private Task<HttpResponseMessage> LoginWithoutCaptchaAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password }, Ct);

    private Task<HttpResponseMessage> LoginWithCaptchaAsync(string email, string password, string captchaToken) =>
        _client.PostAsJsonAsync("/api/v1/auth/login", new { email, password, captchaToken }, Ct);

    private async Task<bool> UserExistsAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(email) is not null;
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString();
    }
}
