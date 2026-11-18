using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Gate;
using TicketRush.Application.Features.Gate.Scan;
using TicketRush.Application.Features.Gate.Staff;

namespace TicketRush.Api.Features.Gate;

public sealed record ScanRequest(string? Payload);

public sealed record AssignGateStaffRequest(string Email, string GateName);

public sealed record RemoveGateStaffRequest(string Email);

/// <summary>
/// Gate check-in (spec 11). The gate routes need the <c>GateStaff</c> role and an assignment to the event (404 otherwise); the
/// organizer routes need the event to be the caller's own. Every answer is <c>no-store</c>.
/// </summary>
public static class GateEndpoints
{
    public static RouteGroupBuilder MapGateEndpoints(this RouteGroupBuilder v1)
    {
        var gate = v1.MapGroup("/gate").WithTags("Gate").RequireAuthorization(AuthPolicies.RequireGateStaff).AddEndpointFilter<NoStoreFilter>();
        gate.MapGet("/events", ListAsync)
            .WithName("ListGateEvents")
            .Produces<IReadOnlyList<GateEventItem>>();
        gate.MapPost("/events/{eventId:guid}/scan", ScanAsync)
            .WithName("ScanTicket")
            .Produces<ScanResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        gate.MapGet("/events/{eventId:guid}/stats", StatsAsync)
            .WithName("GetGateStats")
            .Produces<GateStatsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        var staff = v1.MapGroup("/organizer/events/{id:guid}/gate-staff")
            .WithTags("Gate")
            .RequireAuthorization(AuthPolicies.RequireOrganizer);
        staff.MapPost("/", AssignAsync)
            .WithName("AssignGateStaff")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        staff.MapDelete("/", RemoveAsync)
            .WithName("RemoveGateStaff")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return v1;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, ListGateEventsHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(user.UserId(), ct));

    private static async Task<IResult> ScanAsync(
        Guid eventId,
        ScanRequest request,
        ClaimsPrincipal user,
        IValidator<ScanCommand> validator,
        ScanHandler handler,
        CancellationToken ct)
    {
        var command = new ScanCommand(user.UserId(), eventId, request.Payload);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> StatsAsync(Guid eventId, ClaimsPrincipal user, GetGateStatsHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), eventId, ct)).ToHttpResult();

    private static async Task<IResult> AssignAsync(
        Guid id,
        AssignGateStaffRequest request,
        ClaimsPrincipal user,
        IValidator<AssignGateStaffCommand> validator,
        AssignGateStaffHandler handler,
        CancellationToken ct)
    {
        var command = new AssignGateStaffCommand(user.UserId(), id, request.Email, request.GateName);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> RemoveAsync(
        Guid id,
        [FromBody] RemoveGateStaffRequest request,
        ClaimsPrincipal user,
        IValidator<RemoveGateStaffCommand> validator,
        RemoveGateStaffHandler handler,
        CancellationToken ct)
    {
        var command = new RemoveGateStaffCommand(user.UserId(), id, request.Email);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }
}
