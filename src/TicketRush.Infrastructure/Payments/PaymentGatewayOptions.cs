using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace TicketRush.Infrastructure.Payments;

/// <summary>
/// Mock payment gateway (spec 08). The gateway itself (sessions, signing) is always available because it is the
/// configured provider; <see cref="Enabled"/> only switches on the dev/demo control surface: the api's
/// <c>/mock-gateway</c> endpoints and the worker that delivers the signed webhooks.
/// </summary>
public sealed class MockGatewayOptions
{
    public const string SectionName = "Payments:Mock";

    /// <summary>Maps the control endpoints and starts the delivery worker. Off unless a developer or demo turns it on.</summary>
    public bool Enabled { get; set; }

    /// <summary>Explicit override that lets <see cref="Enabled"/> stand in Production (a public demo). Without it the host refuses to start.</summary>
    public bool AllowInProduction { get; set; }

    /// <summary>HMAC key of the webhook signature. Required outside Development; the worker signs and the api verifies with it.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Public origin of the web app; the redirect URL is <c>{WebBaseUrl}/mock-gateway/{sessionId}</c>.</summary>
    [Required]
    public string WebBaseUrl { get; set; } = "http://localhost:3000";

    /// <summary>Where the worker POSTs webhooks (the api's <c>/api/v1/webhooks/payments/mock</c>).</summary>
    [Required]
    public string WebhookUrl { get; set; } = "http://api:8080/api/v1/webhooks/payments/mock";

    /// <summary>How late <c>succeeded_delayed</c> delivers its webhook (spec 08: 3 minutes, scenario C3).</summary>
    public TimeSpan DelayedWebhookDelay { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How long a session outlives its expiry in Redis, so late lookups still answer.</summary>
    public TimeSpan SessionMargin { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a succeeded session stays refundable.</summary>
    public TimeSpan SucceededRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Webhook deliveries are dropped after this many failed attempts.</summary>
    [Range(1, 50)]
    public int MaxDeliveryAttempts { get; set; } = 8;

    /// <summary>First retry delay of a failed delivery; doubles each attempt up to a minute.</summary>
    public TimeSpan DeliveryRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Fault injection: every create / status / refund call fails as if the provider were down (C11).</summary>
    public bool SimulateOutage { get; set; }
}

/// <summary>Refuses Production configurations that would leave the mock gateway open or unsigned.</summary>
internal sealed class MockGatewayOptionsValidator(IHostEnvironment environment) : IValidateOptions<MockGatewayOptions>
{
    internal const int MinimumSecretLength = 16;

    public ValidateOptionsResult Validate(string? name, MockGatewayOptions options)
    {
        var failures = new List<string>();
        if (options.Enabled && environment.IsProduction() && !options.AllowInProduction)
        {
            failures.Add($"{MockGatewayOptions.SectionName}:Enabled must not be true in Production. Set {MockGatewayOptions.SectionName}:AllowInProduction to override.");
        }

        if (!environment.IsDevelopment() && options.WebhookSecret.Length < MinimumSecretLength)
        {
            failures.Add($"{MockGatewayOptions.SectionName}:WebhookSecret must be at least {MinimumSecretLength} characters outside Development.");
        }

        if (options.Enabled && string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            failures.Add($"{MockGatewayOptions.SectionName}:WebhookSecret is required when {MockGatewayOptions.SectionName}:Enabled is true.");
        }

        RequireAbsoluteHttpUrl(options.WebBaseUrl, nameof(options.WebBaseUrl), failures);
        RequireAbsoluteHttpUrl(options.WebhookUrl, nameof(options.WebhookUrl), failures);
        RequirePositive(options.DelayedWebhookDelay, nameof(options.DelayedWebhookDelay), failures);
        RequirePositive(options.SessionMargin, nameof(options.SessionMargin), failures);
        RequirePositive(options.SucceededRetention, nameof(options.SucceededRetention), failures);
        RequirePositive(options.DeliveryRetryBaseDelay, nameof(options.DeliveryRetryBaseDelay), failures);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void RequireAbsoluteHttpUrl(string value, string property, List<string> failures)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{MockGatewayOptions.SectionName}:{property} must be an absolute http(s) URL.");
        }
    }

    private static void RequirePositive(TimeSpan value, string property, List<string> failures)
    {
        if (value <= TimeSpan.Zero)
        {
            failures.Add($"{MockGatewayOptions.SectionName}:{property} must be positive.");
        }
    }
}

/// <summary>
/// Outbound gateway call policy (C11): per attempt timeout, retries with exponential backoff + jitter, and a circuit
/// breaker that opens when too many calls in the sampling window failed.
/// </summary>
public sealed class GatewayResilienceOptions
{
    public const string SectionName = "Payments:Resilience";

    /// <summary>Timeout of one attempt (spec 08: 10 s).</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:05:00")]
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Retries after the first attempt (spec 08: 2, so three attempts in total).</summary>
    [Range(0, 5)]
    public int RetryCount { get; set; } = 2;

    /// <summary>Delay before the first retry; doubles each time, with jitter.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:00:30")]
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Share of failed attempts in the sampling window that opens the circuit.</summary>
    [Range(0.01, 1.0)]
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Attempts the window must hold before the ratio is evaluated.</summary>
    [Range(2, 1000)]
    public int BreakerMinimumThroughput { get; set; } = 10;

    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long the circuit stays open before one probe call is let through.</summary>
    [Range(typeof(TimeSpan), "00:00:00.500", "00:10:00")]
    public TimeSpan BreakerBreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
