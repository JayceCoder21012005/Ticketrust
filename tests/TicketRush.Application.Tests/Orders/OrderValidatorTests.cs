using TicketRush.Application.Features.Orders;

namespace TicketRush.Application.Tests.Orders;

public sealed class OrderValidatorTests
{
    private static readonly CreateOrderValidator Create = new();

    private static CreateOrderCommand Command(string name = "Nguyễn An", string email = "an@example.com", string? phone = "0901234567", Guid? holdId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), holdId ?? Guid.NewGuid(), new BuyerInput(name, email, phone));

    [Fact]
    public void A_complete_buyer_is_valid()
    {
        Create.Validate(Command()).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0901234567")]
    [InlineData("+84901234567")]
    public void Vietnamese_phone_numbers_are_valid(string phone)
    {
        Create.Validate(Command(phone: phone)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void The_phone_is_optional()
    {
        Create.Validate(Command(phone: null)).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("090123456")]
    [InlineData("+1 202 555 0100")]
    [InlineData("")]
    public void Other_phone_formats_are_rejected_on_buyer_phone(string phone)
    {
        Create.Validate(Command(phone: phone)).Errors.ShouldContain(e => e.PropertyName == "buyer.phone");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_name_is_rejected_on_buyer_name(string name)
    {
        Create.Validate(Command(name: name)).Errors.ShouldContain(e => e.PropertyName == "buyer.name");
    }

    [Fact]
    public void A_name_over_100_characters_is_rejected_and_exactly_100_is_fine()
    {
        Create.Validate(Command(name: new string('a', 100))).IsValid.ShouldBeTrue();
        Create.Validate(Command(name: new string('a', 101))).Errors.ShouldContain(e => e.PropertyName == "buyer.name");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@")]
    public void A_bad_email_is_rejected_on_buyer_email(string email)
    {
        Create.Validate(Command(email: email)).Errors.ShouldContain(e => e.PropertyName == "buyer.email");
    }

    [Fact]
    public void An_empty_hold_id_is_rejected()
    {
        Create.Validate(Command(holdId: Guid.Empty)).Errors.ShouldContain(e => e.PropertyName == "HoldId");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void The_list_limit_is_1_to_50(int limit)
    {
        new ListMyOrdersValidator().Validate(new ListMyOrdersQuery(Guid.NewGuid(), null, limit)).IsValid.ShouldBeFalse();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(50)]
    public void Limits_in_range_are_valid(int limit)
    {
        new ListMyOrdersValidator().Validate(new ListMyOrdersQuery(Guid.NewGuid(), null, limit)).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Garbage_cursors_are_rejected()
    {
        new ListMyOrdersValidator().Validate(new ListMyOrdersQuery(Guid.NewGuid(), "not a cursor!")).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void A_cursor_round_trips_with_full_precision()
    {
        var createdAt = new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        var id = Guid.CreateVersion7();

        OrderCursor.TryDecode(OrderCursor.Encode(createdAt, id), out var cursor).ShouldBeTrue();

        cursor.CreatedAt.ShouldBe(createdAt);
        cursor.Id.ShouldBe(id);
    }

    [Fact]
    public void A_cursor_with_a_non_utc_offset_decodes_to_the_same_instant_in_utc()
    {
        var id = Guid.CreateVersion7();
        var json = $$"""{"CreatedAt":"2026-01-01T07:00:00+07:00","Id":"{{id}}"}""";
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));

        OrderCursor.TryDecode(encoded, out var cursor).ShouldBeTrue();

        cursor.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        cursor.CreatedAt.ShouldBe(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e30")]
    public void Empty_and_content_free_cursors_do_not_decode(string? value)
    {
        OrderCursor.TryDecode(value, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+07:00")]
    [InlineData("0001-01-01T05:00:00+07:00")]
    [InlineData("9999-12-31T23:59:59-07:00")]
    public void A_cursor_whose_instant_is_out_of_the_utc_range_does_not_decode(string createdAt)
    {
        var json = $$"""{"CreatedAt":"{{createdAt}}","Id":"{{Guid.CreateVersion7()}}"}""";
        var encoded = System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(json));

        OrderCursor.TryDecode(encoded, out _).ShouldBeFalse();
    }
}
