using TicketRush.Application.Options;

namespace TicketRush.Application.Tests.Orders;

/// <summary>The refund worker's backoff after a declined refund (spec 08): exponential, capped, never overflowing.</summary>
public sealed class RefundOptionsTests
{
    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 480)]
    [InlineData(6, 960)]
    [InlineData(7, 1920)]
    [InlineData(8, 3600)]
    [InlineData(1_000, 3600)]
    [InlineData(int.MaxValue, 3600)]
    public void The_default_wait_doubles_from_30_seconds_up_to_an_hour(int declines, int seconds)
    {
        new RefundOptions().DelayAfter(declines).ShouldBe(TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void The_defaults_park_a_refund_after_eight_declines_a_little_over_an_hour_after_the_first()
    {
        var options = new RefundOptions();

        options.MaxDeclines.ShouldBe(8);
        Enumerable.Range(1, options.MaxDeclines - 1).Sum(n => options.DelayAfter(n).TotalSeconds).ShouldBe(3810);
    }

    [Fact]
    public void A_zero_or_negative_count_waits_the_base_delay()
    {
        new RefundOptions().DelayAfter(0).ShouldBe(TimeSpan.FromSeconds(30));
    }
}
