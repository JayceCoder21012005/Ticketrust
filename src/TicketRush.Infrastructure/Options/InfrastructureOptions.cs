using System.ComponentModel.DataAnnotations;

namespace TicketRush.Infrastructure.Options;

/// <summary>JWT settings (spec 03). The signing key is a secret: user-secrets / env only, never in git.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = string.Empty;

    [Required]
    public string Audience { get; set; } = string.Empty;

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 10;
}

/// <summary>Payment gateway selection (spec 08).</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    [Required]
    [RegularExpression("^(Mock|VnPay)$")]
    public string Provider { get; set; } = "Mock";

    /// <summary>Must stay below the seat hold TTL (C3).</summary>
    [Range(1, 30)]
    public int PaymentWindowMinutes { get; set; } = 8;
}

/// <summary>Outgoing email (spec 09). SMTP endpoint comes from <c>ConnectionStrings:mailpit</c> in dev.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = string.Empty;

    [Required]
    public string FromName { get; set; } = string.Empty;
}

/// <summary>Blob storage for covers, avatars and ticket PDFs (specs 03, 05, 10).</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    [Required]
    [RegularExpression("^(Local|S3)$")]
    public string Provider { get; set; } = "Local";

    [Required]
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>Local provider only; defaults to <c>App_Data/storage</c> under the content root.</summary>
    public string? LocalRootPath { get; set; }
}
