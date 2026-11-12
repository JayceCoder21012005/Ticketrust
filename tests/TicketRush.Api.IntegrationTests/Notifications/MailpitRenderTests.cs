using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Application.Abstractions.Notifications;
using TicketRush.Domain.Notifications;

namespace TicketRush.Api.IntegrationTests.Notifications;

/// <summary>
/// Spec 09 acceptance criterion 4, end to end: every email template goes through the real <c>EmailDeliveryProcessor</c> and the
/// real MailKit <c>SmtpEmailSender</c> into a Mailpit container, and is read back through Mailpit's HTTP API.
/// </summary>
public sealed partial class MailpitRenderTests(ContainersFixture containers) : IAsyncLifetime
{
    // Pinned on purpose: a moving tag could change the API this test reads.
    private const string Image = "axllent/mailpit:v1.29";
    private const int SmtpPort = 1025;
    private const int HttpPort = 8025;
    private const string LogoUrl = "http://localhost:3000/logo-email.png";
    private const string FromAddress = "no-reply@ticketrush.dev";
    private const string EventTitle = "Đêm nhạc Sơn Tùng M-TP — Sky Tour";
    private const string Name = "Nguyễn Văn Đạt";

    private static readonly string[] Templates =
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

    private static readonly object Model = new
    {
        name = Name,
        eventTitle = EventTitle,
        orderCode = "TR-20261111-0042",
        amount = "1.250.000 ₫",
        startsAtText = "20:00, 15/11/2026",
        link = "http://localhost:3000/account/orders/0196a1c2-0000-7000-8000-000000000001?a=1&b=2",
    };

    private readonly IContainer _mailpit = new ContainerBuilder(Image)
        .WithPortBinding(SmtpPort, assignRandomHostPort: true)
        .WithPortBinding(HttpPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(HttpPort).ForPath("/api/v1/info")))
        .Build();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        try
        {
            await _mailpit.StartAsync(Ct);
        }
        catch (Exception ex) when (ImageCannotBePulled(ex))
        {
            Assert.Skip($"The {Image} image cannot be pulled (no network or registry access): {ex.GetType().Name}");
        }
    }

    public async ValueTask DisposeAsync() => await _mailpit.DisposeAsync();

    [Fact]
    public async Task Every_template_arrives_in_mailpit_with_html_text_and_vietnamese_diacritics()
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(HttpPort)}") };

        var database = await containers.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(NotificationTestHost.Start);
        await using var host = new NotificationTestHost(
            database, containers.RedisConnectionString, sender: null, clock, batchSize: 50,
            smtpConnectionString: $"Endpoint=smtp://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(SmtpPort)}");

        var deliveries = new Dictionary<string, EmailDelivery>();
        await using (var scope = host.Scope())
        {
            var db = NotificationTestHost.Db(scope);
            foreach (var template in Templates)
            {
                var delivery = EmailDelivery.Create(
                    Guid.CreateVersion7(), $"{template}@example.com", template, JsonSerializer.Serialize(Model), $"mailpit:{template}", clock.GetUtcNow());
                deliveries[template] = delivery;
                db.EmailDeliveries.Add(delivery);
            }

            await db.SaveChangesAsync(Ct);
        }

        (await host.Processor.ProcessBatchAsync(Ct)).ShouldBe(Templates.Length);
        (await host.Processor.ProcessBatchAsync(Ct)).ShouldBe(0, "a delivered row is never claimed again, so a second run sends nothing");

        await using (var scope = host.Scope())
        {
            var statuses = await NotificationTestHost.Db(scope).EmailDeliveries.AsNoTracking().Select(d => d.Status).ToListAsync(Ct);
            statuses.ShouldAllBe(status => status == EmailDeliveryStatus.Sent);
        }

        var summaries = (await http.GetFromJsonAsync<JsonObject>("/api/v1/messages?limit=100", Ct))!["messages"]!.AsArray();
        summaries.Count.ShouldBe(Templates.Length, "one message per delivery, none twice");

        var renderer = host.Services.GetRequiredService<IEmailRenderer>();
        foreach (var (template, delivery) in deliveries)
        {
            var messageId = $"{delivery.Id:N}@ticketrush";
            var summary = summaries.Where(m => (string?)m!["MessageID"] == messageId).ShouldHaveSingleItem()!;
            var id = (string)summary["ID"]!;
            var message = (await http.GetFromJsonAsync<JsonObject>($"/api/v1/message/{id}", Ct))!;
            var raw = await http.GetStringAsync($"/api/v1/message/{id}/raw", Ct);
            var expected = renderer.Render(template, Model);

            // Envelope
            ((string)message["From"]!["Address"]!).ShouldBe(FromAddress, template);
            ((string)message["From"]!["Name"]!).ShouldBe("TicketRush", template);
            message["To"]!.AsArray().Select(to => (string)to!["Address"]!).ShouldBe([delivery.ToAddress], template);
            ((string)message["Subject"]!).ShouldBe(expected.Subject, template);
            ((string)message["Subject"]!).ShouldMatch("[^\\u0000-\\u007F]", template);

            // Both parts, each declared as UTF-8
            var html = (string)message["HTML"]!;
            var text = (string)message["Text"]!;
            html.ShouldNotBeNullOrWhiteSpace(template);
            text.ShouldNotBeNullOrWhiteSpace(template);
            raw.ShouldMatch(@"(?im)^Content-Type:\s*text/html;\s*charset=utf-8", template);
            raw.ShouldMatch(@"(?im)^Content-Type:\s*text/plain;\s*charset=utf-8", template);
            html.ShouldContain("charset=\"utf-8\"", customMessage: template);

            // Vietnamese text arrives raw in both parts
            html.ShouldContain($"Xin chào {Name}", customMessage: template);
            text.ShouldContain($"Xin chào {Name}", customMessage: template);
            html.ShouldNotMatch(@"&#\d+;", template);
            if (expected.Text.Contains(EventTitle, StringComparison.Ordinal))
            {
                html.ShouldContain(EventTitle, customMessage: template);
                text.ShouldContain(EventTitle, customMessage: template);
            }

            // The decoded bodies are exactly what the renderer produced (Mailpit normalises line endings)
            Normalise(html).ShouldBe(Normalise(expected.Html), template);
            Normalise(text).ShouldBe(Normalise(expected.Text), template);

            // The logo is the only image
            ImageSource().Matches(html).Select(m => m.Groups[1].Value).ShouldAllBe(src => src == LogoUrl, template);
            ImageSource().Count(html).ShouldBe(1, template);
            message["Attachments"]!.AsArray().ShouldBeEmpty(template);
            message["Inline"]!.AsArray().ShouldBeEmpty(template);
        }
    }

    private static string Normalise(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    /// <summary>Only a registry or network problem skips the test; any other failure to start the container is a real failure.</summary>
    private static bool ImageCannotBePulled(Exception ex) =>
        ex.ToString().Contains("pull", StringComparison.OrdinalIgnoreCase) && ex.ToString().Contains(Image, StringComparison.OrdinalIgnoreCase)
        || ex.GetType().Name is "DockerImageNotFoundException";

    [GeneratedRegex("""<img[^>]*\ssrc="([^"]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSource();
}
