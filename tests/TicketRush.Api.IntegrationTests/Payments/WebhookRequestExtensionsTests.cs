using Microsoft.AspNetCore.Http;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Payments;

namespace TicketRush.Api.IntegrationTests.Payments;

public sealed class WebhookRequestExtensionsTests
{
    [Fact]
    public void Headers_are_copied_and_looked_up_ignoring_case()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers["x-signature"] = "t=1,v1=abc";
        request.Headers["Content-Type"] = "application/json";

        var headers = request.ToWebhookHeaders();

        headers.Get(WebhookHeaders.SignatureHeader).ShouldBe("t=1,v1=abc");
        headers.Get("content-type").ShouldBe("application/json");
        headers.Get("X-Other").ShouldBeNull();
    }
}
