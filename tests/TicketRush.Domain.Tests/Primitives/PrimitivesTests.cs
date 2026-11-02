using TicketRush.Domain.Primitives;

namespace TicketRush.Domain.Tests.Primitives;

public sealed class MoneyTests
{
    [Fact]
    public void Defaults_to_VND_and_adds_amounts()
    {
        var total = new Money(800_000) + new Money(1_500_000);

        total.ShouldBe(new Money(2_300_000, "VND"));
    }

    [Fact]
    public void Multiplies_by_quantity()
    {
        (new Money(800_000) * 3).Amount.ShouldBe(2_400_000);
    }

    [Fact]
    public void Rejects_negative_amounts()
    {
        Should.Throw<DomainException>(() => new Money(-1)).Code.ShouldBe("money.negative");
    }

    [Fact]
    public void Refuses_to_add_different_currencies()
    {
        Should.Throw<DomainException>(() => new Money(1) + new Money(1, "USD")).Code.ShouldBe("money.currency_mismatch");
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping()
    {
        Should.Throw<OverflowException>(() => new Money(long.MaxValue) + new Money(1));
    }
}

public sealed class ResultTests
{
    [Fact]
    public void Success_exposes_the_value()
    {
        Result<int> result = 42;

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_exposes_the_error_and_hides_the_value()
    {
        var error = Error.NotFound("event.not_found", "Not found");
        Result<int> result = error;

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(error);
        Should.Throw<InvalidOperationException>(() => result.Value);
    }
}
