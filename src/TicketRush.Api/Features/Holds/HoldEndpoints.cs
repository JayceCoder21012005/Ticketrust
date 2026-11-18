using TicketRush.Api.Common.RateLimiting;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Api.Features.Queue;
using TicketRush.Application.Abstractions.Inventory;
using TicketRush.Application.Features.Holds;
using TicketRush.Application.Options;

namespace TicketRush.Api.Features.Holds;

public sealed record GaLineRequest(Guid TierId, int Quantity);

/// <param name="SeatIndexes">Seats to hold; any order.</param>
/// <param name="Ga">General-admission quantities.</param>
/// <param name="BestAvailable">Let the server pick N adjacent seats of a seated tier instead of <paramref name="SeatIndexes"/>.</param>
/// <param name="ReplaceExisting">Replace the buyer's current hold (seats in both stay held throughout).</param>
public sealed record CreateHoldRequest(int[]? SeatIndexes, GaLineRequest[]? Ga, BestAvailableRequest? BestAvailable, bool ReplaceExisting);

/// <summary>Seat holds (spec 07). Creating one needs the waiting room's admission; reading and releasing do not.</summary>
public static class HoldEndpoints
{
    public static RouteGroupBuilder MapHoldEndpoints(this RouteGroupBuilder v1)
    {
        var holds = v1.MapGroup("/events/{eventId:guid}/holds").WithTags("Holds").RequireAuthorization();
        var ttl = v1.ServiceProvider().GetRequiredService<IOptions<HoldsOptions>>().Value.Ttl;

        holds.MapPost("/", CreateAsync)
            .RequireRateLimit(RateLimitPolicies.Holds)
            .RequireAdmissionToken()
            .RequireIdempotencyKey(ttl)
            .WithName("CreateHold")
            .Produces<HoldResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        holds.MapGet("/me", GetMineAsync)
            .WithName("GetMyHold")
            .Produces<HoldResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        holds.MapDelete("/me", ReleaseMineAsync)
            .WithName("ReleaseMyHold")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return v1;
    }

    private static async Task<IResult> CreateAsync(
        Guid eventId, CreateHoldRequest request, HttpContext http, ClaimsPrincipal user, CreateHoldHandler handler, CancellationToken ct)
    {
        var command = new CreateHoldCommand(
            user.UserId(),
            eventId,
            http.AdmissionClaims().PhaseId,
            request.SeatIndexes,
            request.Ga?.Select(l => new GaLine(l.TierId, l.Quantity)).ToList(),
            request.BestAvailable,
            request.ReplaceExisting);
        return (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> GetMineAsync(Guid eventId, ClaimsPrincipal user, GetMyHoldHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), eventId, ct)).ToHttpResult();

    private static async Task<IResult> ReleaseMineAsync(Guid eventId, ClaimsPrincipal user, ReleaseMyHoldHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), eventId, ct)).ToHttpResult();

    private static IServiceProvider ServiceProvider(this IEndpointRouteBuilder builder) => builder.ServiceProvider;
}
