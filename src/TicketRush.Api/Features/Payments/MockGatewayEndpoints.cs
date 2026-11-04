using Microsoft.Extensions.Options;
using TicketRush.Api.Common.Http;
using TicketRush.Infrastructure.Payments;

namespace TicketRush.Api.Features.Payments;

/// <param name="Outcome">One of <c>succeeded</c>, <c>failed</c>, <c>succeeded_delayed</c>, <c>succeeded_twice</c>.</param>
public sealed record CompleteMockSessionRequest(MockOutcome Outcome);

/// <summary>
/// Control surface of the mock payment gateway for the dev/demo page <c>/mock-gateway/[sessionId]</c> (spec 08).
/// Mapped only when <c>Payments:Mock:Enabled</c> is true; startup refuses that setting in Production unless
/// <c>Payments:Mock:AllowInProduction</c> is set too. Anonymous on purpose: the session id is the capability, like a
/// real provider's hosted payment page.
/// </summary>
public static class MockGatewayEndpoints
{
    public static RouteGroupBuilder MapMockGatewayEndpoints(this RouteGroupBuilder v1)
    {
        if (!v1.ServiceProvider().GetRequiredService<IOptions<MockGatewayOptions>>().Value.Enabled)
        {
            return v1;
        }

        var sessions = v1.MapGroup("/mock-gateway/sessions").WithTags("MockGateway");
        sessions.MapGet("/{sessionId}", GetAsync)
            .WithName("GetMockGatewaySession")
            .Produces<MockSessionView>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        sessions.MapPost("/{sessionId}/complete", CompleteAsync)
            .WithName("CompleteMockGatewaySession")
            .Produces<MockSessionView>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return v1;
    }

    private static async Task<IResult> GetAsync(string sessionId, MockGatewayControl control, CancellationToken ct) =>
        await control.GetAsync(sessionId, ct) is { } view
            ? TypedResults.Ok(view)
            : MockGatewayErrors.SessionNotFound.ToProblem();

    private static async Task<IResult> CompleteAsync(string sessionId, CompleteMockSessionRequest request, MockGatewayControl control, CancellationToken ct) =>
        (await control.CompleteAsync(sessionId, request.Outcome, ct)).ToHttpResult();

    private static IServiceProvider ServiceProvider(this IEndpointRouteBuilder builder) => builder.ServiceProvider;
}
