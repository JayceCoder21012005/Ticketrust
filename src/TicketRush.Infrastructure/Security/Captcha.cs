using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Infrastructure.Redis;

namespace TicketRush.Infrastructure.Security;

public enum CaptchaProvider
{
    Turnstile,
    Fake,
}

/// <summary>Spec 12 CAPTCHA settings. The Turnstile secret is a secret: AppHost parameter / user-secrets / env only.</summary>
public sealed class CaptchaOptions : IValidatableObject
{
    public const string SectionName = "Captcha";

    /// <summary>The token Cloudflare's test site keys hand out; the fake verifier accepts exactly this one.</summary>
    public const string FakePassToken = "XXXX.DUMMY.TOKEN.XXXX";

    [Required]
    public CaptchaProvider? Provider { get; set; }

    public string? TurnstileSecret { get; set; }

    public Uri TurnstileVerifyUrl { get; set; } = new("https://challenges.cloudflare.com/turnstile/v0/siteverify");

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Provider == CaptchaProvider.Turnstile && string.IsNullOrWhiteSpace(TurnstileSecret))
        {
            yield return new ValidationResult("Captcha:TurnstileSecret is required with the Turnstile provider.", [nameof(TurnstileSecret)]);
        }
    }
}

/// <summary>Accepts the Cloudflare test token only, so a client that skips the widget still fails in dev and tests.</summary>
public sealed class FakeCaptchaVerifier : ICaptchaVerifier
{
    public Task<CaptchaVerdict> VerifyAsync(string? token, CancellationToken ct) =>
        Task.FromResult(token == CaptchaOptions.FakePassToken ? CaptchaVerdict.Passed : CaptchaVerdict.Failed);
}

/// <summary>Cloudflare Turnstile <c>siteverify</c>. Each token is single-use; Cloudflare enforces that.</summary>
public sealed partial class TurnstileVerifier(HttpClient http, IOptions<CaptchaOptions> options, ILogger<TurnstileVerifier> logger) : ICaptchaVerifier
{
    private const int MaxTokenLength = 2048;

    public async Task<CaptchaVerdict> VerifyAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return CaptchaVerdict.Failed;
        }

        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = options.Value.TurnstileSecret!,
                ["response"] = token,
            });
            using var response = await http.PostAsync(options.Value.TurnstileVerifyUrl, form, ct);
            if (!response.IsSuccessStatusCode)
            {
                LogUnavailable((int)response.StatusCode, null);
                return CaptchaVerdict.Unavailable;
            }

            var body = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(ct);
            return body?.Success == true ? CaptchaVerdict.Passed : CaptchaVerdict.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
        {
            LogUnavailable(null, ex);
            return CaptchaVerdict.Unavailable;
        }
    }

    [LoggerMessage(EventId = 1211, Level = LogLevel.Warning, Message = "Turnstile could not verify a token (status {Status}).")]
    private partial void LogUnavailable(int? status, Exception? exception);

    private sealed record SiteVerifyResponse([property: JsonPropertyName("success")] bool Success);
}

/// <summary>A per-email counter with a 15-minute window: <c>tr:{env}:login-fail:{hash}</c>.</summary>
public sealed class RedisLoginFailureCounter(IConnectionMultiplexer redis, RedisKeys keys) : ILoginFailureCounter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public async Task<int> CountAsync(string email, CancellationToken ct)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(keys.LoginFailures(email)).WaitAsync(ct);
            return value.TryParse(out int count) ? count : 0;
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return 0;
        }
    }

    public async Task RecordAsync(string email, CancellationToken ct)
    {
        try
        {
            var db = redis.GetDatabase();
            var key = keys.LoginFailures(email);
            var batch = db.CreateTransaction();
            _ = batch.StringIncrementAsync(key);
            _ = batch.KeyExpireAsync(key, Window);
            await batch.ExecuteAsync().WaitAsync(ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Best effort: losing a count only means one less CAPTCHA.
        }
    }

    public async Task ResetAsync(string email, CancellationToken ct)
    {
        try
        {
            await redis.GetDatabase().KeyDeleteAsync(keys.LoginFailures(email)).WaitAsync(ct);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // The window expires on its own.
        }
    }
}
