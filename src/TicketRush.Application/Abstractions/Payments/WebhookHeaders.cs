namespace TicketRush.Application.Abstractions.Payments;

/// <summary>
/// Request headers of an incoming webhook, with case-insensitive lookup. Application cannot reference ASP.NET Core
/// (layering test), so the webhook endpoint copies <c>HttpRequest.Headers</c> into this type.
/// </summary>
public sealed class WebhookHeaders
{
    public const string SignatureHeader = "X-Signature";

    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public WebhookHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var (name, value) in headers)
        {
            _values[name] = value;
        }
    }

    /// <summary>The header's value, or <c>null</c> when absent.</summary>
    public string? Get(string name) => _values.GetValueOrDefault(name);
}

/// <summary>Replay protection shared by every webhook verifier (spec 08: |now - t| &lt;= 5 minutes).</summary>
public static class WebhookTolerance
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
}
