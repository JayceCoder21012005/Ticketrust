using System.Security.Cryptography;
using System.Text;
using TicketRush.Application.Abstractions.Payments;
using TicketRush.Infrastructure.Payments;

namespace TicketRush.Api.IntegrationTests.Payments;

/// <summary>Spec 08: <c>X-Signature: t=&lt;unix&gt;,v1=&lt;HMAC-SHA256(secret, t + "." + body)&gt;</c>, |now - t| &lt;= 5 min.</summary>
public sealed class WebhookSignatureTests
{
    private const string Secret = "unit-test-webhook-secret-0123456789";
    private const string Body = """{"id":"evt_1","type":"payment.succeeded","sessionId":"mock_1"}""";
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = WebhookTolerance.Window;

    private static string Hmac(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    private static void Verify(string? header, string body = Body, DateTimeOffset? now = null) =>
        WebhookSignature.Verify(header, body, Secret, now ?? Now, Tolerance);

    [Fact]
    public void The_window_is_five_minutes()
    {
        WebhookTolerance.Window.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Sign_produces_the_documented_format()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);

        header.ShouldBe($"t={Now.ToUnixTimeSeconds()},v1={Hmac(Secret, $"{Now.ToUnixTimeSeconds()}.{Body}")}");
    }

    [Fact]
    public void A_good_signature_passes()
    {
        Should.NotThrow(() => Verify(WebhookSignature.Sign(Secret, Body, Now)));
    }

    [Fact]
    public void Uppercase_hex_is_accepted()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);
        var upper = header[..(header.IndexOf("v1=", StringComparison.Ordinal) + 3)] + header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..].ToUpperInvariant();

        Should.NotThrow(() => Verify(upper));
    }

    [Fact]
    public void A_signature_made_with_another_secret_is_rejected()
    {
        var header = WebhookSignature.Sign("some-other-secret-0123456789", Body, Now);

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(header));
    }

    [Fact]
    public void A_tampered_body_is_rejected()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(header, Body.Replace("succeeded", "failed", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_tampered_timestamp_is_rejected()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);
        var shifted = header.Replace($"t={Now.ToUnixTimeSeconds()}", $"t={Now.ToUnixTimeSeconds() + 1}", StringComparison.Ordinal);

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(shifted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(63)]
    public void Flipping_any_single_hex_digit_is_rejected(int index)
    {
        var header = WebhookSignature.Sign(Secret, Body, Now);
        var start = header.IndexOf("v1=", StringComparison.Ordinal) + 3;
        var chars = header.ToCharArray();
        chars[start + index] = chars[start + index] == '0' ? '1' : '0';

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(new string(chars)));
    }

    [Fact]
    public void Exactly_five_minutes_old_or_ahead_is_accepted()
    {
        Should.NotThrow(() => Verify(WebhookSignature.Sign(Secret, Body, Now - Tolerance)));
        Should.NotThrow(() => Verify(WebhookSignature.Sign(Secret, Body, Now + Tolerance)));
    }

    [Fact]
    public void A_validly_signed_message_older_than_five_minutes_is_rejected()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now - Tolerance - TimeSpan.FromSeconds(1));

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(header)).Message.ShouldContain("window");
    }

    [Fact]
    public void A_validly_signed_message_from_more_than_five_minutes_ahead_is_rejected()
    {
        var header = WebhookSignature.Sign(Secret, Body, Now + Tolerance + TimeSpan.FromSeconds(1));

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(header)).Message.ShouldContain("window");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("t=abc,v1=00")]
    [InlineData("t=,v1=")]
    [InlineData("v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1793786400")]
    [InlineData("t=1793786400,v1=not-hex")]
    [InlineData("t=1793786400,v1=0000")]
    [InlineData("t=-1,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("t=1793786400,t=1793786400,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("=1,v1=0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData(",,,")]
    public void A_malformed_header_is_rejected_without_any_other_exception(string? header)
    {
        Should.Throw<InvalidWebhookSignatureException>(() => Verify(header));
    }

    [Fact]
    public void Unknown_schemes_are_ignored_and_any_matching_v1_is_enough()
    {
        var good = WebhookSignature.Sign(Secret, Body, Now);
        var stale = $"v1={new string('0', 64)}";

        Should.NotThrow(() => Verify($"{stale},v0=ignored,{good}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Signing_with_a_blank_secret_is_refused(string secret)
    {
        Should.Throw<ArgumentException>(() => WebhookSignature.Sign(secret, Body, Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Verifying_with_a_blank_secret_fails_closed_even_for_a_signature_made_with_that_key(string secret)
    {
        // An empty key is a valid HMAC key, so a forged "valid" header is easy to build by hand.
        var forged = $"t={Now.ToUnixTimeSeconds()},v1={Hmac(secret, $"{Now.ToUnixTimeSeconds()}.{Body}")}";

        Should.Throw<ArgumentException>(() => WebhookSignature.Verify(forged, Body, secret, Now, Tolerance));
        Should.Throw<ArgumentException>(() => WebhookSignature.Verify(null, Body, secret, Now, Tolerance));
    }

    [Fact]
    public void A_null_secret_is_refused_too()
    {
        Should.Throw<ArgumentNullException>(() => WebhookSignature.Sign(null!, Body, Now));
        Should.Throw<ArgumentNullException>(() => WebhookSignature.Verify("t=1,v1=00", Body, null!, Now, Tolerance));
    }

    [Fact]
    public void Verification_is_constant_time_by_construction()
    {
        // Timing cannot be asserted reliably in a unit test, so pin the construction: the comparison is
        // CryptographicOperations.FixedTimeEquals over decoded bytes, never string equality. Equal-length wrong
        // signatures that differ in the first and in the last byte must therefore fail the same way.
        var header = WebhookSignature.Sign(Secret, Body, Now);
        var start = header.IndexOf("v1=", StringComparison.Ordinal) + 3;
        var firstByteWrong = header[..start] + (header[start] == 'a' ? "bb" : "aa") + header[(start + 2)..];
        var lastByteWrong = header[..^2] + (header[^1] == 'a' ? "bb" : "aa");

        Should.Throw<InvalidWebhookSignatureException>(() => Verify(firstByteWrong)).Message.ShouldContain("does not match");
        Should.Throw<InvalidWebhookSignatureException>(() => Verify(lastByteWrong)).Message.ShouldContain("does not match");
    }
}
