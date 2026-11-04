using System.ComponentModel.DataAnnotations;
using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Holds;

public sealed class HoldsOptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var options = new HoldsOptions();

        options.TtlSeconds.ShouldBe(480);
        options.MaxSeatsPerHold.ShouldBe(8);
        options.Ttl.ShouldBe(TimeSpan.FromMinutes(8));
    }

    [Theory]
    [InlineData(30, 8)]
    [InlineData(480, 9)]
    [InlineData(480, 0)]
    public void Out_of_range_values_fail_validation(int ttlSeconds, int maxSeats)
    {
        var options = new HoldsOptions { TtlSeconds = ttlSeconds, MaxSeatsPerHold = maxSeats };

        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true).ShouldBeFalse();
    }
}
