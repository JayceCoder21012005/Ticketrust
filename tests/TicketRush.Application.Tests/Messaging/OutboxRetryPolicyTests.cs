using TicketRush.Application.Abstractions.Messaging;

namespace TicketRush.Application.Tests.Messaging;

public sealed class OutboxRetryPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(5, 16)]
    public void Delay_doubles_per_attempt_with_jitter_in_the_upper_half(int attempts, int ceilingSeconds)
    {
        var ceiling = TimeSpan.FromSeconds(ceilingSeconds);

        OutboxRetryPolicy.NextDelay(attempts, random: 0).ShouldBe(ceiling / 2);
        OutboxRetryPolicy.NextDelay(attempts, random: 0.999).ShouldBeInRange(ceiling / 2, ceiling);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(1_000)]
    public void Delay_is_capped_at_ten_minutes(int attempts)
    {
        OutboxRetryPolicy.NextDelay(attempts, random: 1).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Ten_attempts_exhaust_the_message()
    {
        OutboxRetryPolicy.IsExhausted(9).ShouldBeFalse();
        OutboxRetryPolicy.IsExhausted(10).ShouldBeTrue();
    }
}
