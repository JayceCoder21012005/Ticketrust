using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Users;

/// <summary>Error codes of spec 03. Messages are safe to show; the web maps codes to Vietnamese copy.</summary>
public static class AuthErrors
{
    /// <summary>Same error for an unknown email and a wrong password, so responses don't reveal accounts.</summary>
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden("auth.email_not_confirmed", "Confirm your email address first.");

    public static readonly Error LockedOut =
        Error.Forbidden("auth.locked_out", "Too many failed attempts. Try again later.");

    public static readonly Error RefreshInvalid =
        Error.Unauthorized("auth.refresh_invalid", "Your session has ended. Please sign in again.");

    public static readonly Error InvalidToken =
        Error.Validation("auth.invalid_token", "The link is invalid or has expired.");

    public static readonly Error MissingCsrfHeader =
        Error.Forbidden("auth.csrf_header_missing", "Missing X-Requested-With header.");

    public static Error WeakPassword(IReadOnlyDictionary<string, string[]> details) =>
        Error.Validation("auth.weak_password", "Password is too weak.", details);
}
