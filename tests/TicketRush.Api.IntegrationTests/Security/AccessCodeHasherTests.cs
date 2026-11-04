using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using TicketRush.Infrastructure.Options;
using TicketRush.Infrastructure.Security;

namespace TicketRush.Api.IntegrationTests.Security;

public sealed class AccessCodeHasherTests
{
    private static AccessCodeHasher With(string pepper) =>
        new(Options.Create(new CatalogOptions { AccessCodePepper = pepper }));

    [Fact]
    public void Hash_is_case_and_whitespace_insensitive()
    {
        var hasher = With("pepper-0123456789abcdef0123456789abcdef");

        hasher.Hash(" abc ").ShouldBe(hasher.Hash("ABC"));
        hasher.Hash("ABC").ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    public void Hash_depends_on_pepper() =>
        With("pepper-one-0123456789abcdef0123456789").Hash("ABC")
            .ShouldNotBe(With("pepper-two-0123456789abcdef0123456789").Hash("ABC"));

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void A_missing_or_short_pepper_fails_validation(string pepper)
    {
        var options = new CatalogOptions { AccessCodePepper = pepper };

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeFalse();
    }
}
