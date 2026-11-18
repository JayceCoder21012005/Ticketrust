using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Tickets;
using TicketRush.Application.Features.Tickets.GetQrKey;
using TicketRush.Application.Features.Tickets.GetTicket;
using TicketRush.Application.Features.Tickets.ListMyTickets;

namespace TicketRush.Api.Features.Tickets;

/// <summary>Query string of <c>GET /me/tickets</c>.</summary>
public sealed record ListMyTicketsRequest([property: FromQuery(Name = "scope")] string? Scope);

/// <summary>
/// The ticket wallet (spec 10): the caller's tickets, one ticket, and the secret its rotating QR is derived from. Owner only (another
/// user's ticket is 404, like an unknown id). Every answer is <c>no-store</c>.
/// </summary>
public static class TicketEndpoints
{
    public static RouteGroupBuilder MapTicketEndpoints(this RouteGroupBuilder v1)
    {
        var tickets = v1.MapGroup(string.Empty).WithTags("Tickets").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();

        tickets.MapGet("/me/tickets", ListAsync)
            .WithName("ListMyTickets")
            .Produces<MyTicketsResponse>()
            .ProducesValidationProblem();
        tickets.MapGet("/tickets/{id:guid}", GetAsync)
            .WithName("GetTicket")
            .Produces<TicketDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        tickets.MapGet("/tickets/{id:guid}/qr-key", GetQrKeyAsync)
            .WithName("GetTicketQrKey")
            .Produces<QrKeyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return v1;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListMyTicketsRequest request,
        ClaimsPrincipal user,
        IValidator<ListMyTicketsQuery> validator,
        ListMyTicketsHandler handler,
        CancellationToken ct)
    {
        var query = new ListMyTicketsQuery(user.UserId(), request.Scope);
        return await validator.ValidateToProblemAsync(query, ct) ?? TypedResults.Ok(await handler.HandleAsync(query, ct));
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, GetTicketHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> GetQrKeyAsync(Guid id, ClaimsPrincipal user, GetQrKeyHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();
}
