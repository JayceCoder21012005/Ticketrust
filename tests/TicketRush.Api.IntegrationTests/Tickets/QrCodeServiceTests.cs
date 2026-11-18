using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TicketRush.Application.Abstractions.Tickets;
using TicketRush.Infrastructure.Tickets;

namespace TicketRush.Api.IntegrationTests.Tickets;

/// <summary>
/// Pure unit tests (no containers). The vectors file <c>shared/fixtures/qr-vectors.json</c> is shared with the web tests,
/// which must reproduce every code exactly; set <c>UPDATE_QR_VECTORS=1</c> to rewrite it from the seed after an intended change.
/// </summary>
public sealed class QrCodeServiceTests
{
    private const int VectorSeed = 20261112;
    private const int VectorCount = 1000;

    private static readonly Guid Ticket = Guid.Parse("0192f0e0-aaaa-7bbb-8ccc-0123456789ab");
    private static readonly byte[] Secret = [.. Enumerable.Range(0, 32).Select(i => (byte)i)];
    private const long Now = 1_780_000_005;
    private const string ExpectedAtNow = "TR1.AZLw4Kqqe7uMzAEjRWeJqw.118666667.4NhM5CKlFVlALQ";

    private readonly QrCodeService _qr = new();

    private static string B64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private ParsedQrCode Parse(string code)
    {
        _qr.TryParse(code, out var parsed).ShouldBeTrue(code);
        return parsed!;
    }

    [Fact]
    public void Encode_matches_the_documented_example()
    {
        // Hand-computed: independent HMAC call, network-order guid bytes, canonical lowercase guid text.
        var step = Now / IQrCodeService.StepSeconds;
        var mac = HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes($"{Ticket:D}|{step}"));
        var expected = $"TR1.{B64Url(Ticket.ToByteArray(bigEndian: true))}.{step}.{B64Url(mac.AsSpan(0, 10))}";

        _qr.Encode(Ticket, Secret, Now).ShouldBe(expected);
        _qr.Encode(Ticket, Secret, Now).ShouldBe(ExpectedAtNow, "literal produced by a separate Python implementation");
        step.ShouldBe(118666667);
    }

    [Fact]
    public void Encode_has_fixed_widths_and_uses_the_whole_step_window()
    {
        var code = _qr.Encode(Ticket, Secret, Now);
        var parts = code.Split('.');

        parts.Length.ShouldBe(4);
        parts[0].ShouldBe(IQrCodeService.Format);
        parts[1].Length.ShouldBe(22);
        parts[3].Length.ShouldBe(14);
        code.Length.ShouldBeLessThan(128);

        // Same step for the whole 15 s window, next step on the boundary.
        var start = Now / 15 * 15;
        _qr.Encode(Ticket, Secret, start).ShouldBe(_qr.Encode(Ticket, Secret, start + 14));
        _qr.Encode(Ticket, Secret, start + 15).ShouldNotBe(_qr.Encode(Ticket, Secret, start + 14));
    }

    [Fact]
    public void Encode_writes_step_zero_without_padding()
    {
        var code = _qr.Encode(Ticket, Secret, 14);

        code.Split('.')[2].ShouldBe("0");
        Parse(code).Step.ShouldBe(0);
    }

    [Fact]
    public void Encode_rejects_negative_time()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => _qr.Encode(Ticket, Secret, -1));
    }

    [Fact]
    public void Code_verifies_at_the_previous_current_and_next_step()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, Now));
        var stepStart = Now / 15 * 15;

        _qr.Verify(parsed, Secret, stepStart - 15).ShouldBeTrue("next step is still ahead of the code by one");
        _qr.Verify(parsed, Secret, stepStart).ShouldBeTrue("current step");
        _qr.Verify(parsed, Secret, stepStart + 14).ShouldBeTrue("end of the current step");
        _qr.Verify(parsed, Secret, stepStart + 15).ShouldBeTrue("code is one step old");
        _qr.Verify(parsed, Secret, stepStart + 29).ShouldBeTrue("end of the previous-step window");
    }

    [Fact]
    public void Code_fails_two_steps_away()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, Now));
        var stepStart = Now / 15 * 15;

        _qr.Verify(parsed, Secret, stepStart + 30).ShouldBeFalse("N+2");
        _qr.Verify(parsed, Secret, stepStart - 16).ShouldBeFalse("N-2");
        _qr.Verify(parsed, Secret, stepStart + 3600).ShouldBeFalse();
    }

    [Fact]
    public void Tampered_signature_or_ticket_id_fails_verification()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, Now));

        var flipped = (byte[])parsed.Signature.Clone();
        flipped[0] ^= 0x01;
        _qr.Verify(parsed with { Signature = flipped }, Secret, Now).ShouldBeFalse();
        _qr.Verify(parsed with { Signature = parsed.Signature[..9] }, Secret, Now).ShouldBeFalse();
        _qr.Verify(parsed with { Signature = [] }, Secret, Now).ShouldBeFalse();
        _qr.Verify(parsed with { TicketId = Guid.Parse("0192f0e0-aaaa-7bbb-8ccc-0123456789ac") }, Secret, Now).ShouldBeFalse();
        _qr.Verify(parsed with { Step = parsed.Step + 1 }, Secret, Now).ShouldBeFalse("same signature for another step");
    }

    [Fact]
    public void Another_secret_fails_verification()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, Now));
        var other = (byte[])Secret.Clone();
        other[31] ^= 0xFF;

        _qr.Verify(parsed, other, Now).ShouldBeFalse();
    }

    [Fact]
    public void Verify_rejects_negative_time()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, 0));

        _qr.Verify(parsed, Secret, -1).ShouldBeFalse();
    }

    [Fact]
    public void Parse_round_trips_the_fields()
    {
        var parsed = Parse(_qr.Encode(Ticket, Secret, Now));

        parsed.TicketId.ShouldBe(Ticket);
        parsed.Step.ShouldBe(Now / 15);
        parsed.Signature.Length.ShouldBe(10);
    }

    public static TheoryData<string> MalformedCodes()
    {
        var qr = new QrCodeService();
        var good = qr.Encode(Ticket, Secret, Now);
        var p = good.Split('.');
        var id = p[1];
        var sig = p[3];
        var step = p[2];
        var guidBytes = Ticket.ToByteArray(bigEndian: true);

        return new TheoryData<string>
        {
            "",
            " ",
            "TR1",
            "TR2." + string.Join('.', p[1..]),
            "tr1." + string.Join('.', p[1..]),
            $"{id}.{step}.{sig}",
            $"TR1.{id}.{step}",
            $"{good}.extra",
            $"{good}.",
            $"TR1.{id}.{step}.{sig}.{sig}",
            $"TR1.!{id[1..]}.{step}.{sig}",
            $"TR1.{id[..^1]}+.{step}.{sig}",
            $"TR1.{id[..^1]}/.{step}.{sig}",
            $"TR1.{id}.{step}.{sig[..^1]}=",
            $"TR1.{B64Url(Ticket.ToByteArray(bigEndian: true))}==.{step}.{sig}",
            $"TR1.{B64Url(guidBytes.AsSpan(0, 15))}.{step}.{sig}",
            $"TR1.{B64Url([.. guidBytes, 0])}.{step}.{sig}",
            $"TR1.{id}.{step}.{B64Url(new byte[9])}",
            $"TR1.{id}.{step}.{B64Url(new byte[11])}",
            $"TR1.{id}..{sig}",
            $"TR1.{id}.-1.{sig}",
            $"TR1.{id}.+1.{sig}",
            $"TR1.{id}.0{step}.{sig}",
            $"TR1.{id}.00.{sig}",
            $"TR1.{id}.1e5.{sig}",
            $"TR1.{id}.abc.{sig}",
            $"TR1.{id}.1.5.{sig}",
            $"TR1.{id}.{step} .{sig}",
            $"TR1.{id}.9223372036854775808.{sig}",
            $"TR1.{id}.{new string('9', 40)}.{sig}",
            $"TR1.{id}.١٢٣.{sig}",
            good + new string('A', 200),
            "TR1." + new string('A', 5000),
            new string('.', 10_000),
            $" {good}",
            $"{good} ",
            $"{good}\n",
            $"{good}\r\n",
            $"TR1.{id}.{step}.{sig[..7]} {sig[7..]}",
            $"TR1.{id}.{step}.{sig}\0",
            $"TR1.{id[..^1]}é.{step}.{sig}",
            $"TR1.{id}.{step}.{sig[..^1]}😀",
            "TR1.\uD800.0.x",
        };
    }

    [Theory]
    [MemberData(nameof(MalformedCodes))]
    public void Parse_rejects_malformed_codes(string code)
    {
        _qr.TryParse(code, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Fact]
    public void Parse_rejects_null_without_throwing()
    {
        _qr.TryParse(null!, out var parsed).ShouldBeFalse();
        parsed.ShouldBeNull();
    }

    [Fact]
    public void Parse_rejects_non_canonical_base64_that_decodes_to_the_same_bytes()
    {
        // The last base64 char of a 16- or 10-byte value carries 4 unused bits; a lenient decoder would accept variants.
        var p = _qr.Encode(Ticket, Secret, Now).Split('.');
        var altId = p[1][..^1] + NextAlphabetChar(p[1][^1]);
        var altSig = p[3][..^1] + NextAlphabetChar(p[3][^1]);

        _qr.TryParse($"TR1.{altId}.{p[2]}.{p[3]}", out _).ShouldBeFalse();
        _qr.TryParse($"TR1.{p[1]}.{p[2]}.{altSig}", out _).ShouldBeFalse();
    }

    private static char NextAlphabetChar(char c) =>
        c == 'A' ? 'B' : (char)(c - 1 >= 'A' ? c - 1 : c + 1);

    [Fact]
    public void Parse_accepts_the_largest_step_that_fits_a_long()
    {
        var p = _qr.Encode(Ticket, Secret, Now).Split('.');

        _qr.TryParse($"TR1.{p[1]}.{long.MaxValue}.{p[3]}", out var parsed).ShouldBeTrue();
        parsed.Step.ShouldBe(long.MaxValue);
        _qr.Verify(parsed, Secret, Now).ShouldBeFalse("far in the future");
    }

    [Fact]
    public void Vectors_file_verifies_every_case()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorsPath()));
        var root = doc.RootElement;
        root.GetProperty("stepSeconds").GetInt32().ShouldBe(IQrCodeService.StepSeconds);
        root.GetProperty("format").GetString().ShouldBe(IQrCodeService.Format);
        var cases = root.GetProperty("cases");
        cases.GetArrayLength().ShouldBeGreaterThanOrEqualTo(1000);

        foreach (var c in cases.EnumerateArray())
        {
            var id = Guid.Parse(c.GetProperty("ticketId").GetString()!);
            var secret = Convert.FromBase64String(c.GetProperty("secret").GetString()!);
            var unix = c.GetProperty("unixSeconds").GetInt64();
            var code = c.GetProperty("code").GetString()!;

            secret.Length.ShouldBe(32);
            _qr.Encode(id, secret, unix).ShouldBe(code);
            var parsed = Parse(code);
            parsed.TicketId.ShouldBe(id);
            parsed.Step.ShouldBe(unix / 15);
            _qr.Verify(parsed, secret, unix).ShouldBeTrue(code);
            _qr.Verify(parsed, secret, unix + 30).ShouldBeFalse(code);
        }
    }

    [Fact]
    public void Vectors_file_is_reproducible_from_its_seed()
    {
        var path = VectorsPath(mustExist: false);
        if (Environment.GetEnvironmentVariable("UPDATE_QR_VECTORS") == "1")
        {
            File.WriteAllText(path, GenerateVectors(VectorSeed, VectorCount));
        }

        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var seed = doc.RootElement.GetProperty("seed").GetInt32();

        seed.ShouldBe(VectorSeed);
        Normalize(text).ShouldBe(Normalize(GenerateVectors(seed, VectorCount)));
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>Deterministic: every ticket id, secret and time comes from one seeded <see cref="Random"/>.</summary>
    private static string GenerateVectors(int seed, int count)
    {
        var rng = new Random(seed);
        var qr = new QrCodeService();
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\n")
          .Append(inv, $"  \"seed\": {seed},\n")
          .Append(inv, $"  \"stepSeconds\": {IQrCodeService.StepSeconds},\n")
          .Append(inv, $"  \"format\": \"{IQrCodeService.Format}\",\n")
          .Append("  \"cases\": [\n");

        for (var i = 0; i < count; i++)
        {
            var idBytes = new byte[16];
            var secret = new byte[32];
            rng.NextBytes(idBytes);
            rng.NextBytes(secret);
            var id = new Guid(idBytes);
            var unix = VectorTime(rng, i);
            var code = qr.Encode(id, secret, unix);
            sb.Append(inv, $"    {{ \"ticketId\": \"{id:D}\", \"secret\": \"{Convert.ToBase64String(secret)}\", \"unixSeconds\": {unix}, \"code\": \"{code}\" }}");
            sb.Append(i < count - 1 ? ",\n" : "\n");
        }

        return sb.Append("  ]\n}\n").ToString();
    }

    private static long VectorTime(Random rng, int i)
    {
        if (i == 0)
        {
            return 0;
        }

        var step = rng.NextInt64(1, 4_000_000_000L / 15);
        return (i % 4) switch
        {
            0 => rng.NextInt64(0, 4_000_000_000L),
            1 => step * 15,
            2 => (step * 15) - 1,
            _ => rng.NextInt64(1_700_000_000L, 1_900_000_000L),
        };
    }

    private static string VectorsPath(bool mustExist = true)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TicketRush.slnx")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("repo root (TicketRush.slnx) not found above the test binaries");
        var path = Path.Combine(dir.FullName, "shared", "fixtures", "qr-vectors.json");
        if (mustExist)
        {
            File.Exists(path).ShouldBeTrue($"{path} is missing; run the tests once with UPDATE_QR_VECTORS=1");
        }

        return path;
    }
}
