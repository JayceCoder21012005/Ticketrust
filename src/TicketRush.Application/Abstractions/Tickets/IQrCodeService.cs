using System.Diagnostics.CodeAnalysis;

namespace TicketRush.Application.Abstractions.Tickets;

/// <summary>A syntactically valid <c>TR1</c> code. The signature is not checked yet: pass it to <see cref="IQrCodeService.Verify"/>.</summary>
public sealed record ParsedQrCode(Guid TicketId, long Step, byte[] Signature);

/// <summary>
/// The rotating ticket QR (spec 10). Payload <c>TR1.&lt;ticketId b64url&gt;.&lt;step&gt;.&lt;sig b64url&gt;</c>, where the signature is
/// the first 10 bytes of HMAC-SHA256(ticket secret, <c>"{ticketId:D}|{step}"</c>) and <c>step = floor(unixSeconds / 15)</c>.
/// Pure: no I/O, no clock (callers pass the time). The browser computes the same string from the same secret.
/// </summary>
public interface IQrCodeService
{
    /// <summary>Seconds a code stays the same; the step counter advances at this rate.</summary>
    public const int StepSeconds = 15;

    /// <summary>The payload prefix and version tag.</summary>
    public const string Format = "TR1";

    string Encode(Guid ticketId, ReadOnlySpan<byte> secret, long unixSeconds);

    /// <summary>Parses the shape only; never throws. False for any malformed, oversized or non-canonical input.</summary>
    bool TryParse(string code, [NotNullWhen(true)] out ParsedQrCode? parsed);

    /// <summary>True when the signature matches the secret and the step is within one step of <paramref name="unixSeconds"/>.</summary>
    bool Verify(ParsedQrCode parsed, ReadOnlySpan<byte> secret, long unixSeconds);
}
