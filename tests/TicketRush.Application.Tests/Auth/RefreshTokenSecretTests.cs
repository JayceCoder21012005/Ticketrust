using TicketRush.Application.Features.Auth;

namespace TicketRush.Application.Tests.Auth;

public sealed class RefreshTokenSecretTests
{
    [Fact]
    public void Generate_returns_256_random_bits_as_base64url()
    {
        var secret = RefreshTokenSecret.Generate();

        secret.Length.ShouldBe(43); // 32 bytes, base64url without padding
        secret.ShouldMatch("^[A-Za-z0-9_-]+$");
        RefreshTokenSecret.Generate().ShouldNotBe(secret);
    }

    [Fact]
    public void Hash_is_the_lowercase_hex_sha256_of_the_token()
    {
        // echo -n "abc" | sha256sum
        RefreshTokenSecret.Hash("abc").ShouldBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }
}
