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

/// <summary>Admission token settings (spec 06). Deliberately a different key from <see cref="JwtOptions"/>.</summary>
public sealed class AdmissionTokenOptions
{
    public const string SectionName = "AdmissionToken";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "ticketrush-api";

    [Required]
    public string Audience { get; set; } = "ticketrush-admission";
}

/// <summary>Payment gateway selection (spec 08).</summary>
public sealed class PaymentOptions
{
    public const string SectionName = "Payment";

    [Required]
    [RegularExpression("^(Mock|MoMo|VnPay)$")]
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

/// <summary>Organizer catalog settings (spec 05). The pepper is a secret: AppHost parameter / user-secrets / env only.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    [Required]
    [MinLength(32)]
    public string AccessCodePepper { get; set; } = string.Empty;
}

/// <summary>
/// The email delivery worker (spec 09). The retry schedule is its own, not the outbox's: an SMTP outage of hours must only delay
/// email, so the defaults (30 s doubling to 1 h, 30 attempts) keep a delivery alive for most of a day.
/// </summary>
public sealed class EmailDeliveryOptions : IValidatableObject
{
    public const string SectionName = "EmailDelivery";

    /// <summary>Deliveries handled per <c>ProcessBatchAsync</c> call, one transaction each.</summary>
    [Range(1, 200)]
    public int BatchSize { get; set; } = 20;

    [Range(typeof(TimeSpan), "00:00:00.050", "00:01:00")]
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Longest one render-and-send may take; past it the delivery counts as failed and is rescheduled.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:10:00")]
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after the first failed send; it doubles per failure (with jitter) up to <see cref="MaxDelay"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Failed sends after which a delivery is dead-lettered.</summary>
    [Range(1, 100)]
    public int MaxAttempts { get; set; } = 30;

    /// <summary>Consecutive transport failures (connection, timeout, protocol, 4xx) that end a pass: SMTP itself is probably down.</summary>
    [Range(1, 100)]
    public int OutageFailureThreshold { get; set; } = 5;

    /// <summary>Longest pause of the worker loop while SMTP looks down; the pause doubles from 5 s up to this.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan OutageMaxBackoff { get; set; } = TimeSpan.FromMinutes(5);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MaxDelay < BaseDelay)
        {
            yield return new ValidationResult("EmailDelivery:MaxDelay must not be shorter than EmailDelivery:BaseDelay.", [nameof(MaxDelay), nameof(BaseDelay)]);
        }
    }
}
