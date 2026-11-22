using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Payments.MoMo;

namespace TicketRush.Api.IntegrationTests.Payments.MoMo;

/// <summary>Spec 18: MoMo's signing rule (HMAC-SHA256, lowercase hex, alphabetical <c>k=v</c> pairs joined by <c>&amp;</c>).</summary>
public sealed class MoMoSignatureTests
{
    /// <summary>MoMo's public sandbox secret (published in MoMo's own sample code).</summary>
    private const string SandboxSecret = "K951B6PE1waDMi640xX08PD3vg6EkVlz";

    /// <summary>The create request MoMo's sandbox answered with resultCode 0 on 2026-11-22 (spec 18 spike).</summary>
    private static readonly (string, string)[] AcceptedCreate =
    [
        ("requestType", "payWithMethod"),
        ("partnerCode", "MOMO"),
        ("accessKey", "F8BBA842ECF85"),
        ("amount", "10000"),
        ("extraData", ""),
        ("ipnUrl", "https://example.com/ipn"),
        ("orderId", "TR78ade6f952d84d4a8f951d9f8b4a7a"),
        ("orderInfo", "TicketRush test"),
        ("redirectUrl", "https://example.com/return"),
        ("requestId", "3877a124-e7c2-4b99-8261-d3a5f06ff7bf"),
    ];

    [Fact]
    public void Sign_matches_the_value_MoMo_accepted_in_the_sandbox()
    {
        var raw = MoMoSignature.Raw(AcceptedCreate);

        raw.ShouldBe("accessKey=F8BBA842ECF85&amount=10000&extraData=&ipnUrl=https://example.com/ipn&orderId=TR78ade6f952d84d4a8f951d9f8b4a7a"
            + "&orderInfo=TicketRush test&partnerCode=MOMO&redirectUrl=https://example.com/return&requestId=3877a124-e7c2-4b99-8261-d3a5f06ff7bf"
            + "&requestType=payWithMethod");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SandboxSecret), Encoding.UTF8.GetBytes(raw)));
        MoMoSignature.Sign(raw, SandboxSecret).ShouldBe(expected);
    }

    [Fact]
    public void Raw_orders_keys_alphabetically_whatever_the_argument_order()
    {
        MoMoSignature.Raw(("orderId", "1"), ("accessKey", "a"), ("requestId", "r"), ("partnerCode", "p"))
            .ShouldBe("accessKey=a&orderId=1&partnerCode=p&requestId=r");
    }

    [Fact]
    public void Matches_rejects_null_wrong_and_non_hex_and_accepts_uppercase_hex()
    {
        const string raw = "accessKey=a&orderId=1";
        var signature = MoMoSignature.Sign(raw, SandboxSecret);

        MoMoSignature.Matches(raw, SandboxSecret, signature).ShouldBeTrue();
        MoMoSignature.Matches(raw, SandboxSecret, signature.ToUpperInvariant()).ShouldBeTrue();
        MoMoSignature.Matches(raw, SandboxSecret, null).ShouldBeFalse();
        MoMoSignature.Matches(raw, SandboxSecret, "").ShouldBeFalse();
        MoMoSignature.Matches(raw, SandboxSecret, "zz" + signature[2..]).ShouldBeFalse();
        MoMoSignature.Matches(raw, SandboxSecret, signature[..^2]).ShouldBeFalse();
        MoMoSignature.Matches(raw + "&x=1", SandboxSecret, signature).ShouldBeFalse();
        MoMoSignature.Matches(raw, "another-secret", signature).ShouldBeFalse();
    }
}

/// <summary>Spec 18: <c>Payments:MoMo</c> is only demanded when MoMo is the configured provider.</summary>
public sealed class MoMoOptionsTests
{
    private static ValidateOptionsResult Validate(string provider, string environment, Action<MoMoOptions>? change = null)
    {
        var options = new MoMoOptions
        {
            PartnerCode = "MOMO",
            AccessKey = "F8BBA842ECF85",
            SecretKey = "K951B6PE1waDMi640xX08PD3vg6EkVlz",
            IpnUrl = "https://tickets.example.com/api/v1/webhooks/payments/momo",
        };
        change?.Invoke(options);
        return new MoMoOptionsValidator(new FakeEnvironment(environment), Options.Create(new PaymentOptions { Provider = provider }))
            .Validate(null, options);
    }

    [Fact]
    public void Validator_requires_keys_only_when_MoMo_is_the_provider()
    {
        Validate("Mock", "Production", o => { o.PartnerCode = o.AccessKey = o.SecretKey = o.IpnUrl = ""; }).Succeeded.ShouldBeTrue();
        Validate("MoMo", "Production").Succeeded.ShouldBeTrue();
        Validate("momo", "Production", o => o.SecretKey = "").FailureMessage!.ShouldContain("SecretKey");
        Validate("MoMo", "Production", o => o.AccessKey = " ").FailureMessage!.ShouldContain("AccessKey");
        Validate("MoMo", "Production", o => o.PartnerCode = "").FailureMessage!.ShouldContain("PartnerCode");
    }

    [Fact]
    public void Validator_wants_https_urls_outside_Development()
    {
        Validate("MoMo", "Production", o => o.IpnUrl = "http://tickets.example.com/ipn").FailureMessage!.ShouldContain("IpnUrl");
        Validate("MoMo", "Production", o => o.Endpoint = "http://test-payment.momo.vn").FailureMessage!.ShouldContain("Endpoint");
        Validate("MoMo", "Production", o => o.IpnUrl = "not a url").FailureMessage!.ShouldContain("IpnUrl");
        Validate("MoMo", "Development", o => o.IpnUrl = "http://localhost:5080/api/v1/webhooks/payments/momo").Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validator_wants_a_sane_amount_range()
    {
        Validate("MoMo", "Production", o => o.MinAmount = 0).FailureMessage!.ShouldContain("MinAmount");
        Validate("MoMo", "Production", o => { o.MinAmount = 5000; o.MaxAmount = 5000; }).FailureMessage!.ShouldContain("MaxAmount");
    }
}
