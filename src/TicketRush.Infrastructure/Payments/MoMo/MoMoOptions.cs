using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Options;

namespace TicketRush.Infrastructure.Payments.MoMo;

/// <summary>
/// MoMo AIO v2 gateway (spec 18). Only demanded when <c>Payment:Provider</c> is <c>MoMo</c>. The keys come from the
/// environment (<c>.env</c>), never from appsettings; MoMo's public sandbox keys are named in the README.
/// </summary>
public sealed class MoMoOptions
{
    public const string SectionName = "Payments:MoMo";

    /// <summary>API host: <c>https://test-payment.momo.vn</c> (sandbox) or <c>https://payment.momo.vn</c>.</summary>
    public string Endpoint { get; set; } = "https://test-payment.momo.vn";

    public string PartnerCode { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    /// <summary>Where MoMo POSTs the payment result: the api's <c>/api/v1/webhooks/payments/momo</c>, reachable from the internet.</summary>
    public string IpnUrl { get; set; } = string.Empty;

    /// <summary>Per attempt; MoMo asks its partners to wait at least 30 s for an answer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Smallest amount MoMo takes for a payment, in VND.</summary>
    public long MinAmount { get; set; } = 1_000;

    /// <summary>Largest amount MoMo takes for a payment, in VND.</summary>
    public long MaxAmount { get; set; } = 50_000_000;

    /// <summary>How long a created session (its payUrl) stays cached past its expiry, for repeated calls with the same key.</summary>
    public TimeSpan SessionMargin { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Refuses to start a MoMo-configured host without keys, with a plain-http endpoint in production, or with a bad amount range.</summary>
internal sealed class MoMoOptionsValidator(IHostEnvironment environment, IOptions<PaymentOptions> payment) : IValidateOptions<MoMoOptions>
{
    public ValidateOptionsResult Validate(string? name, MoMoOptions options)
    {
        if (!string.Equals(payment.Value.Provider, "MoMo", StringComparison.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        Require(options.PartnerCode, nameof(options.PartnerCode), failures);
        Require(options.AccessKey, nameof(options.AccessKey), failures);
        Require(options.SecretKey, nameof(options.SecretKey), failures);
        RequireUrl(options.Endpoint, nameof(options.Endpoint), failures);
        RequireUrl(options.IpnUrl, nameof(options.IpnUrl), failures);
        if (options.Timeout <= TimeSpan.Zero)
        {
            failures.Add($"{MoMoOptions.SectionName}:Timeout must be positive.");
        }

        if (options.MinAmount < 1)
        {
            failures.Add($"{MoMoOptions.SectionName}:MinAmount must be at least 1.");
        }

        if (options.MaxAmount <= options.MinAmount)
        {
            failures.Add($"{MoMoOptions.SectionName}:MaxAmount must be greater than MinAmount.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Require(string value, string property, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{MoMoOptions.SectionName}:{property} is required when Payment:Provider is MoMo.");
        }
    }

    private void RequireUrl(string value, string property, List<string> failures)
    {
        var ok = Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && environment.IsDevelopment()));
        if (!ok)
        {
            failures.Add($"{MoMoOptions.SectionName}:{property} must be an absolute https URL (http only in Development).");
        }
    }
}
