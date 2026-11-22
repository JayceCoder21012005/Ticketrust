using System.Security.Cryptography;
using System.Text;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>
/// MoMo's signing rule (spec 18): the signed fields as <c>key=value</c> pairs, keys in alphabetical order, joined by <c>&amp;</c>,
/// values as sent (never URL-encoded); HMAC-SHA256 with the partner's secret key, lowercase hex. Callers list the fields, never
/// their order, so no field order lives in a string literal.
/// </summary>
public static class MoMoSignature
{
    public static string Raw(params (string Key, string Value)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return string.Join('&', pairs.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}"));
    }

    public static string Sign(string raw, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        return Convert.ToHexStringLower(Hash(raw, secretKey));
    }

    /// <summary>True when <paramref name="signature"/> is the hex HMAC of <paramref name="raw"/> (either case); compared in fixed time.</summary>
    public static bool Matches(string raw, string secretKey, string? signature)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        if (string.IsNullOrEmpty(signature) || signature.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }

        byte[] given;
        try
        {
            given = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(given, Hash(raw, secretKey));
    }

    private static byte[] Hash(string raw, string secretKey) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey), Encoding.UTF8.GetBytes(raw));
}
