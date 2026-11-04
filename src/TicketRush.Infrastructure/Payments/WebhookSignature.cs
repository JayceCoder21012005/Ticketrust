using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// <c>X-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256(secret, t + "." + body)&gt;</c> (spec 08). The signature
/// is compared in constant time and the timestamp must be within the tolerance of the verifier's clock (replay protection).
/// </summary>
public static class WebhookSignature
{
    private const int Sha256HexLength = 64;

    public static string Sign(string secret, string body, DateTimeOffset at)
    {
        var timestamp = at.ToUnixTimeSeconds();
        return string.Create(CultureInfo.InvariantCulture, $"t={timestamp},v1={Convert.ToHexStringLower(Compute(secret, timestamp, body))}");
    }

    /// <exception cref="InvalidWebhookSignatureException">Malformed header, wrong signature or a timestamp outside <paramref name="tolerance"/>.</exception>
    public static void Verify(string? header, string body, string secret, DateTimeOffset now, TimeSpan tolerance)
    {
        var (timestamp, candidates) = Parse(header);
        var expected = Compute(secret, timestamp, body);

        // Every candidate is compared, none short-circuits on content: only the number of v1 entries is observable.
        var matched = false;
        foreach (var candidate in candidates)
        {
            matched |= CryptographicOperations.FixedTimeEquals(candidate, expected);
        }

        if (!matched)
        {
            throw new InvalidWebhookSignatureException("Webhook signature does not match.");
        }

        if (Math.Abs(now.ToUnixTimeSeconds() - timestamp) > tolerance.TotalSeconds)
        {
            throw new InvalidWebhookSignatureException("Webhook timestamp is outside the allowed window.");
        }
    }

    private static byte[] Compute(string secret, long timestamp, string body) =>
        HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{body}")));

    private static (long Timestamp, List<byte[]> Signatures) Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new InvalidWebhookSignatureException("Missing signature header.");
        }

        long? timestamp = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                throw new InvalidWebhookSignatureException("Malformed signature header.");
            }

            var value = part[(separator + 1)..];
            switch (part[..separator])
            {
                case "t":
                    timestamp = timestamp is null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t)
                        ? t
                        : throw new InvalidWebhookSignatureException("Malformed signature timestamp.");
                    break;
                case "v1":
                    signatures.Add(DecodeHex(value));
                    break;
            }
        }

        return timestamp is { } found && signatures.Count > 0
            ? (found, signatures)
            : throw new InvalidWebhookSignatureException("Signature header needs t and v1.");
    }

    private static byte[] DecodeHex(string value)
    {
        var bytes = new byte[Sha256HexLength / 2];
        return value.Length == Sha256HexLength && Convert.FromHexString(value, bytes, out _, out _) == OperationStatus.Done
            ? bytes
            : throw new InvalidWebhookSignatureException("Malformed signature value.");
    }
}
