using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Email;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>Spec 09 email rendering: every template renders both parts with Vietnamese copy, escapes HTML values and shares one layout.</summary>
public sealed partial class EmailRenderingTests
{
    private const string BaseUrl = "http://localhost:3000";
    private const string LayoutFooter = "Email này được gửi tự động bởi TicketRush";

    private static readonly Guid OrderId = Guid.Parse("0196a1c2-0000-7000-8000-000000000001");

    private readonly ScribanEmailRenderer _renderer = new(Options.Create(new FrontendOptions { BaseUrl = BaseUrl }));

    public static TheoryData<string> Templates() =>
    [
        "confirm-email",
        "reset-password",
        "order-paid",
        "refund-requested",
        "refunded",
        "event-cancelled",
        "reminder-sale-opens",
        "reminder-event-starts",
    ];

    private static object Model(string name = "Nguyễn Văn Đạt") => new
    {
        name,
        eventTitle = "Đêm nhạc Mùa Thu",
        orderCode = "TR-20261111-0042",
        amount = "1.250.000 ₫",
        startsAtText = "20:00, 15/11/2026",
        link = $"{BaseUrl}/account/orders/{OrderId}?a=1&b=2",
    };

    [Theory]
    [MemberData(nameof(Templates))]
    public void Every_template_renders_html_and_text_with_vietnamese_diacritics(string template)
    {
        var email = _renderer.Render(template, Model());

        email.Html.ShouldContain("Xin chào Nguyễn Văn Đạt");
        email.Text.ShouldContain("Xin chào Nguyễn Văn Đạt");
        email.Subject.ShouldNotBeNullOrWhiteSpace();
        email.Subject.ShouldNotContain("\n");
        email.Subject.ShouldNotContain("{{");
        email.Html.ShouldNotContain("{{");
        email.Text.ShouldNotContain("{{");
        email.Text.ShouldNotContain("Subject:");
    }

    [Fact]
    public void Subject_is_the_first_line_of_the_text_template_and_not_part_of_its_body()
    {
        var email = _renderer.Render("order-paid", Model());

        email.Subject.ShouldContain("TR-20261111-0042");
        email.Text.ShouldNotContain(email.Subject);
        email.Text.ShouldStartWith("Xin chào");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Html_values_are_escaped_and_text_values_are_not(string template)
    {
        var email = _renderer.Render(template, Model("<script>alert(1)</script>"));

        email.Html.ShouldContain("&lt;script&gt;");
        email.Html.ShouldNotContain("<script>");
        email.Text.ShouldContain("<script>alert(1)</script>");
        email.Subject.ShouldNotContain("&lt;");
    }

    [Fact]
    public void Links_are_escaped_in_html_attributes_but_verbatim_in_text()
    {
        var email = _renderer.Render("order-paid", Model());

        email.Html.ShouldContain($"href=\"{BaseUrl}/account/orders/{OrderId}?a=1&amp;b=2\"");
        email.Text.ShouldContain($"{BaseUrl}/account/orders/{OrderId}?a=1&b=2");
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Html_has_no_remote_image_except_the_logo(string template)
    {
        var email = _renderer.Render(template, Model());

        var sources = ImageSource().Matches(email.Html).Select(m => m.Groups[1].Value).ToList();
        sources.ShouldNotBeEmpty();
        sources.ShouldAllBe(src => src == $"{BaseUrl}/logo-email.png");
        email.Html.ShouldNotContain("url(");
        email.Html.ShouldNotContain("<style");
    }

    [Fact]
    public void Order_paid_has_xem_ve_link_and_no_qr()
    {
        var email = _renderer.Render("order-paid", Model());

        email.Html.ShouldContain("Xem vé");
        email.Html.ShouldContain($"/account/orders/{OrderId}");
        email.Text.ShouldContain($"/account/orders/{OrderId}");
        email.Html.ShouldNotContain("qr", Case.Insensitive);
        email.Text.ShouldNotContain("qr", Case.Insensitive);
        email.Subject.ShouldNotContain("qr", Case.Insensitive);
    }

    [Theory]
    [MemberData(nameof(Templates))]
    public void Layout_is_shared(string template)
    {
        var email = _renderer.Render(template, Model());

        email.Html.ShouldStartWith("<!DOCTYPE html>");
        email.Html.ShouldContain("charset=\"utf-8\"");
        email.Html.ShouldContain(LayoutFooter);
        email.Text.ShouldNotContain(LayoutFooter);
    }

    [Fact]
    public void Reset_email_states_one_hour()
    {
        var email = _renderer.Render("reset-password", Model());

        email.Html.ShouldContain("1 giờ");
        email.Text.ShouldContain("1 giờ");
    }

    [Fact]
    public void Confirm_email_states_three_days()
    {
        var email = _renderer.Render("confirm-email", Model());

        email.Html.ShouldContain("3 ngày");
        email.Text.ShouldContain("3 ngày");
    }

    [Fact]
    public void Unknown_template_is_rejected()
    {
        Should.Throw<ArgumentException>(() => _renderer.Render("nope", Model()));
    }

    [GeneratedRegex("""<img[^>]*\ssrc="([^"]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();
}
