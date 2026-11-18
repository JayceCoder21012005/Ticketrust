using TicketRush.Api.Common.RateLimiting;
using System.Security.Claims;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Queue;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Features.Queue;

/// <param name="AccessCode">Presale code, only for phases that have one.</param>
/// <param name="CaptchaToken">Turnstile token (spec 12), needed to enter the line; not needed to come back to it.</param>
public sealed record JoinQueueRequest(string? AccessCode, string? CaptchaToken);

/// <summary>Virtual waiting room (spec 06). Signed-in buyers only; every call is served from Redis after the first.</summary>
public static class QueueEndpoints
{
    public static RouteGroupBuilder MapQueueEndpoints(this RouteGroupBuilder v1)
    {
        var queue = v1.MapGroup("/events/{eventId:guid}/queue").WithTags("Queue").RequireAuthorization();

        queue.MapPost("/join", JoinAsync)
            .WithName("JoinQueue")
            .RequireRateLimit(RateLimitPolicies.QueueJoin)
            .Produces<QueueStatusResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        queue.MapGet("/me", StatusAsync)
            .WithName("GetQueueStatus")
            .RequireRateLimit(RateLimitPolicies.QueueStatus)
            .Produces<QueueStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        queue.MapPost("/leave", LeaveAsync)
            .WithName("LeaveQueue")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return v1;
    }

    private static async Task<IResult> JoinAsync(
        Guid eventId,
        JoinQueueRequest? request,
        ClaimsPrincipal user,
        JoinQueueHandler handler,
        CancellationToken ct)
    {
        // Registration grants email_verified=false until the link is clicked; the claim is a JSON boolean.
        var verified = bool.TryParse(user.FindFirstValue(AuthClaims.EmailVerified), out var value) && value;
        var command = new JoinQueueCommand(user.UserId(), eventId, verified, request?.AccessCode, request?.CaptchaToken);
        return (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> StatusAsync(Guid eventId, ClaimsPrincipal user, GetQueueStatusHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), eventId, ct)).ToHttpResult();

    private static async Task<IResult> LeaveAsync(Guid eventId, ClaimsPrincipal user, LeaveQueueHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), eventId, ct)).ToHttpResult();
}
