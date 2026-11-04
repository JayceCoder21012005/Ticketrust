using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketRush.Application.Abstractions.Security;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Security;

/// <summary>SHA-256(pepper ‖ UPPER(TRIM(code))). The pepper is a secret, so a leaked table can't be brute-forced offline.</summary>
public sealed class AccessCodeHasher(IOptions<CatalogOptions> options) : IAccessCodeHasher
{
    private readonly byte[] _pepper = Encoding.UTF8.GetBytes(options.Value.AccessCodePepper);

    public string Hash(string code)
    {
        var normalized = Encoding.UTF8.GetBytes(code.Trim().ToUpperInvariant());
        var input = new byte[_pepper.Length + normalized.Length];
        _pepper.CopyTo(input, 0);
        normalized.CopyTo(input, _pepper.Length);
        return Convert.ToHexStringLower(SHA256.HashData(input));
    }
}
