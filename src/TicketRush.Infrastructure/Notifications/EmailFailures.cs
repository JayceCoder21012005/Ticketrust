namespace TicketRush.Infrastructure.Notifications;

/// <summary>How a failed delivery is treated.</summary>
internal enum EmailFailureKind
{
    /// <summary>Retrying cannot help (the template or model cannot render, the server refused this message or address for good): dead-letter now.</summary>
    Permanent,

    /// <summary>SMTP itself is unreachable or not taking mail right now: retry, and count towards the outage breaker.</summary>
    Transport,

    /// <summary>Anything else: retry with backoff, without assuming an outage.</summary>
    Retryable,
}

/// <summary>Sorts a delivery failure into <see cref="EmailFailureKind"/>, separately for the render and the send step.</summary>
internal static class EmailFailures
{
    /// <summary>Rendering is deterministic: the same template and stored model fail the same way on every attempt.</summary>
    public static EmailFailureKind OfRender(Exception error) => error switch
    {
        ArgumentException or System.Text.Json.JsonException or InvalidOperationException => EmailFailureKind.Permanent,
        Scriban.Syntax.ScriptRuntimeException => EmailFailureKind.Permanent,
        _ => EmailFailureKind.Retryable,
    };

    public static EmailFailureKind OfSend(Exception error) => error switch
    {
        MailKit.Net.Smtp.SmtpCommandException command => OfCommand(command),
        MimeKit.ParseException => EmailFailureKind.Permanent, // the stored address cannot be parsed
        System.Net.Sockets.SocketException or IOException or TimeoutException => EmailFailureKind.Transport,
        MailKit.Net.Smtp.SmtpProtocolException or MailKit.ServiceNotConnectedException => EmailFailureKind.Transport,
        MailKit.Security.AuthenticationException or MailKit.Security.SslHandshakeException => EmailFailureKind.Transport,
        _ => EmailFailureKind.Retryable,
    };

    /// <summary>
    /// A 5xx refusal of this recipient or this message is final. A 5xx refusal of our sender or of the session (say "530
    /// authentication required") would fail every email alike, so it is treated as an outage of the sending side instead of
    /// dead-lettering the whole queue one row at a time. Every 4xx is temporary by definition.
    /// </summary>
    private static EmailFailureKind OfCommand(MailKit.Net.Smtp.SmtpCommandException error) =>
        (int)error.StatusCode >= 500
        && error.ErrorCode is MailKit.Net.Smtp.SmtpErrorCode.RecipientNotAccepted or MailKit.Net.Smtp.SmtpErrorCode.MessageNotAccepted
            ? EmailFailureKind.Permanent
            : EmailFailureKind.Transport;
}
