using TicketRush.Application.Features.Notifications.ListNotifications;
using TicketRush.Application.Features.Orders;

namespace TicketRush.Application.Tests.Notifications;

public sealed class ListNotificationsQueryTests
{
    private static readonly ListNotificationsValidator Validator = new();

    private static ListNotificationsQuery Query(string? cursor = null, int limit = ListNotificationsQuery.DefaultLimit) => new(Guid.NewGuid(), cursor, limit);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 20)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(5000, 100)]
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    public void Limit_is_clamped_between_1_and_100(int requested, int expected)
    {
        Query(limit: requested).EffectiveLimit.ShouldBe(expected);
    }

    [Fact]
    public void Defaults_are_20_and_100()
    {
        ListNotificationsQuery.DefaultLimit.ShouldBe(20);
        ListNotificationsQuery.MaxLimit.ShouldBe(100);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_cursor_is_valid(string? cursor)
    {
        Validator.Validate(Query(cursor)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void A_cursor_made_by_the_keyset_codec_is_valid()
    {
        var cursor = OrderCursor.Encode(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero), Guid.NewGuid());

        Validator.Validate(Query(cursor)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("not-a-cursor")]
    [InlineData("e30")]
    [InlineData("!!!")]
    public void Anything_else_is_an_invalid_cursor(string cursor)
    {
        Validator.Validate(Query(cursor)).Errors.ShouldContain(e => e.PropertyName == "Cursor");
    }
}
