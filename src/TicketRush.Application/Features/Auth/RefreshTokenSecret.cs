using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace TicketRush.Application.Features.Auth;

/// <summary>The raw refresh token goes to the client cookie only; the database keeps its SHA-256 hash.</summary>
public static class RefreshTokenSecret
{
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
