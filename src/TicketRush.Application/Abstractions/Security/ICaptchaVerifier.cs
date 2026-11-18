using TicketRush.Domain.Primitives;

namespace TicketRush.Application.Abstractions.Security;

public enum CaptchaVerdict
{
    Passed,

    /// <summary>No token, a token the provider rejected, or one already used.</summary>
    Failed,

    /// <summary>The provider could not be asked: the caller answers 503 and the client tries again.</summary>
    Unavailable,
}

/// <summary>Spec 12 CAPTCHA (Strategy): <c>TurnstileVerifier</c> in production, <c>FakeCaptchaVerifier</c> for dev and tests.</summary>
public interface ICaptchaVerifier
{
    Task<CaptchaVerdict> VerifyAsync(string? token, CancellationToken ct);
}

/// <summary>Failed sign-ins per email (existing or not, so the answer never tells whether an account exists).</summary>
public interface ILoginFailureCounter
{
    /// <summary>Failures in the current window; 0 when the counter cannot be read (a counter outage must not block sign-in).</summary>
    Task<int> CountAsync(string email, CancellationToken ct);

    Task RecordAsync(string email, CancellationToken ct);

    Task ResetAsync(string email, CancellationToken ct);
}

public static class CaptchaErrors
{
    /// <summary>Sent when a CAPTCHA is needed and none came: the client shows the widget and tries again.</summary>
    public static readonly Error Required = Error.Validation("captcha.required", "Please complete the verification.");

    public static readonly Error Failed = Error.Validation("captcha.failed", "The verification did not succeed. Please try again.");

    /// <summary>Maps a verdict to the error to return, or <c>null</c> when it passed.</summary>
    public static Error? For(CaptchaVerdict verdict) => verdict switch
    {
        CaptchaVerdict.Passed => null,
        CaptchaVerdict.Unavailable => CommonErrors.SystemBusy,
        _ => Failed,
    };
}
