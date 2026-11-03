using TicketRush.Application.Features.Catalog;

namespace TicketRush.Application.Tests.Catalog;

public sealed class CatalogQueryTests
{
    private static readonly ListEventsValidator Validator = new();

    [Fact]
    public void Cursor_round_trips_starts_at_and_id()
    {
        var startsAt = new DateTimeOffset(2026, 12, 1, 12, 30, 0, TimeSpan.Zero);
        var id = Guid.CreateVersion7();

        var encoded = EventCursor.Encode(startsAt, id);

        EventCursor.TryDecode(encoded, out var decoded).ShouldBeTrue();
        decoded.ShouldBe(new EventCursor(startsAt, id));
        encoded.ShouldNotContain("=");
        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("e30")] // "{}"
    [InlineData("bnVsbA")] // "null"
    public void Cursor_rejects_garbage(string value)
    {
        EventCursor.TryDecode(value, out _).ShouldBeFalse();
    }

    [Fact]
    public void Validator_accepts_an_empty_query()
    {
        Validator.Validate(new ListEventsQuery()).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validator_rejects_a_bad_cursor_an_unknown_category_and_an_inverted_date_range()
    {
        var from = new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero);
        var result = Validator.Validate(new ListEventsQuery(Category: "karaoke", From: from, To: from.AddDays(-1), Cursor: "zzz", PriceMax: -1, Limit: 51));

        result.Errors.Select(e => e.PropertyName).ShouldBe(["Category", "To", "PriceMax", "Cursor", "Limit"], ignoreOrder: true);
    }

    [Fact]
    public void Validator_rejects_a_search_longer_than_100_characters()
    {
        Validator.Validate(new ListEventsQuery(Q: new string('a', 101))).Errors.ShouldHaveSingleItem().PropertyName.ShouldBe("Q");
    }

    [Fact]
    public void Normalized_drops_blank_search_and_filters_and_clamps_nothing_else()
    {
        var query = new ListEventsQuery(Q: "  Sơn Tùng  ", City: " ", Category: "");

        query.Normalized().ShouldBe(new ListEventsQuery(Q: "Sơn Tùng"));
    }
}
