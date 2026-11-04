using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketRush.Application.Options;
using TicketRush.Infrastructure.Catalog;

namespace TicketRush.Api.IntegrationTests.Outbox;

public sealed class WebCacheRevalidatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebCacheRevalidator Create(StubHandler handler, string? url = "http://web:3000/api/revalidate", string? secret = "s3cret") =>
        new(new HttpClient(handler), Options.Create(new FrontendOptions { BaseUrl = "http://localhost:3000", RevalidateUrl = url, RevalidateSecret = secret }), NullLogger<WebCacheRevalidator>.Instance);

    [Fact]
    public async Task Revalidation_posts_tags_with_secret()
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await Create(handler).RevalidateAsync(["events:list", "event:abc"], Ct);

        var request = handler.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri.ShouldBe(new Uri("http://web:3000/api/revalidate"));
        request.Headers.GetValues("x-revalidate-secret").ShouldBe(["s3cret"]);
        JsonDocument.Parse(handler.Bodies.Single()).RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString())
            .ShouldBe(["events:list", "event:abc"]);
    }

    [Fact]
    public async Task Revalidation_throws_on_500_so_outbox_retries()
    {
        await Should.ThrowAsync<HttpRequestException>(() => Create(new StubHandler(HttpStatusCode.InternalServerError)).RevalidateAsync(["events:list"], Ct));
    }

    [Theory]
    [InlineData(null, "s3cret")]
    [InlineData("http://web:3000/api/revalidate", "")]
    public async Task Revalidation_is_skipped_when_not_configured(string? url, string? secret)
    {
        var handler = new StubHandler(HttpStatusCode.OK);

        await Create(handler, url, secret).RevalidateAsync(["events:list"], Ct);

        handler.Requests.ShouldBeEmpty();
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status);
        }
    }
}
