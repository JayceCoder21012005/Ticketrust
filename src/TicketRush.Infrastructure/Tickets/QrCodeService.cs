using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TicketRush.Application.Abstractions.Tickets;

namespace TicketRush.Infrastructure.Tickets;

/// <summary>
/// Implements the pinned <c>TR1</c> format (see <see cref="IQrCodeService"/>). The ticket id travels as the 16 RFC 4122 bytes
/// in network order, the signed text uses the lowercase hyphenated guid and the decimal step, so the browser can reproduce it.
/// </summary>
public sealed class QrCodeService : IQrCodeService
{
    private const int MaxLength = 128;
    private const int TicketIdBytes = 16;
    private const int SignatureBytes = 10;
    private const int MaxStepDigits = 19;

    public string Encode(Guid ticketId, ReadOnlySpan<byte> secret, long unixSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unixSeconds);
        var step = unixSeconds / IQrCodeService.StepSeconds;

        Span<byte> id = stackalloc byte[TicketIdBytes];
        ticketId.TryWriteBytes(id, bigEndian: true, out _);
        Span<byte> signature = stackalloc byte[SignatureBytes];
        Sign(ticketId, step, secret, signature);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{IQrCodeService.Format}.{Base64Url.EncodeToString(id)}.{step}.{Base64Url.EncodeToString(signature)}");
    }

    public bool TryParse(string code, [NotNullWhen(true)] out ParsedQrCode? parsed)
    {
        parsed = null;
        if (code is not { Length: > 0 and <= MaxLength })
        {
            return false;
        }

        var parts = code.Split('.');
        if (parts.Length != 4
            || parts[0] != IQrCodeService.Format
            || !TryDecode(parts[1], TicketIdBytes, out var id)
            || !TryParseStep(parts[2], out var step)
            || !TryDecode(parts[3], SignatureBytes, out var signature))
        {
            return false;
        }

        parsed = new ParsedQrCode(new Guid(id, bigEndian: true), step, signature);
        return true;
    }

    public bool Verify(ParsedQrCode parsed, ReadOnlySpan<byte> secret, long unixSeconds)
    {
        if (parsed.Signature is not { Length: SignatureBytes } || unixSeconds < 0)
        {
            return false;
        }

        var current = unixSeconds / IQrCodeService.StepSeconds;
        if (parsed.Step < current - 1 || parsed.Step > current + 1)
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[SignatureBytes];
        Sign(parsed.TicketId, parsed.Step, secret, expected);
        return CryptographicOperations.FixedTimeEquals(expected, parsed.Signature);
    }

    private static void Sign(Guid ticketId, long step, ReadOnlySpan<byte> secret, Span<byte> destination)
    {
        var message = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{ticketId:D}|{step}"));
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(secret, message, mac);
        mac[..SignatureBytes].CopyTo(destination);
    }

    /// <summary>Strict, canonical base64url: exact length, alphabet only, no padding, and zero trailing bits (re-encodes to the input).</summary>
    private static bool TryDecode(string text, int byteCount, [NotNullWhen(true)] out byte[]? bytes)
    {
        bytes = null;
        if (text.Length != Base64Url.GetEncodedLength(byteCount) || !text.All(IsBase64UrlChar))
        {
            return false;
        }

        // Base64Url.TryDecodeFromChars throws on bad input, so decode through the non-throwing standard-base64 API.
        Span<char> standard = stackalloc char[(text.Length + 3) / 4 * 4];
        standard.Fill('=');
        for (var i = 0; i < text.Length; i++)
        {
            standard[i] = text[i] switch { '-' => '+', '_' => '/', var c => c };
        }

        var buffer = new byte[byteCount];
        if (!Convert.TryFromBase64Chars(standard, buffer, out var written) || written != byteCount
            || !string.Equals(Base64Url.EncodeToString(buffer), text, StringComparison.Ordinal))
        {
            return false;
        }

        bytes = buffer;
        return true;
    }

    private static bool IsBase64UrlChar(char c) => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';

    /// <summary>Plain decimal digits only: no sign, no leading zero (except "0"), fits a long.</summary>
    private static bool TryParseStep(string text, out long step)
    {
        step = 0;
        return text is { Length: > 0 and <= MaxStepDigits }
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out step);
    }
}
