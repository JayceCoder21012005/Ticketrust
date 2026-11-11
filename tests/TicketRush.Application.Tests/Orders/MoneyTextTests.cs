using TicketRush.Application.Features.Notifications;

namespace TicketRush.Application.Tests.Orders;

public sealed class MoneyTextTests
{
    [Theory]
    [InlineData(0, "VND", "0 ₫")]
    [InlineData(999, "VND", "999 ₫")]
    [InlineData(1000, "VND", "1.000 ₫")]
    [InlineData(1_250_000, "VND", "1.250.000 ₫")]
    [InlineData(1_250_000, "vnd", "1.250.000 ₫")]
    [InlineData(1_250_000, "USD", "1.250.000 USD")]
    public void Format_writes_whole_dong_with_dots_and_other_currencies_with_their_code(long amount, string currency, string expected) =>
        MoneyText.Format(amount, currency).ShouldBe(expected);
}
