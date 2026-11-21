using System.Security.Claims;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Api.Common.Idempotency;
using TicketRush.Api.Common.RateLimiting;
using TicketRush.Application.Features.Orders;
using TicketRush.Application.Features.Resale;
using TicketRush.Domain.Resale;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Features.Resale;

/// <param name="Price">Whole dong, 1 to the ticket's face value.</param>
public sealed record CreateListingRequest(long Price);

public sealed record CreateTransferRequest(string ToEmail);

/// <summary>Resale and transfer (spec 14). The public listing read is anonymous; everything else is the caller's own.</summary>
public static class ResaleEndpoints
{
    public static RouteGroupBuilder MapResaleEndpoints(this RouteGroupBuilder v1)
    {
        var tickets = v1.MapGroup("/tickets/{id:guid}").WithTags("Resale").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();
        tickets.MapPost("/listings", CreateListingAsync)
            .WithName("CreateListing")
            .Produces<ListingResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        tickets.MapPost("/transfers", CreateTransferAsync)
            .WithName("CreateTransfer")
            .Produces<TransferResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        v1.MapGet("/events/{id:guid}/listings", ListAsync)
            .WithName("ListEventListings")
            .WithTags("Resale")
            .AllowAnonymous()
            .AddEndpointFilter<NoStoreFilter>()
            .Produces<IReadOnlyList<ListingTierGroup>>();

        var listings = v1.MapGroup("/listings/{id:guid}").WithTags("Resale").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();
        listings.MapDelete("/", CancelListingAsync)
            .WithName("CancelListing")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        listings.MapPost("/reserve", ReserveAsync)
            .RequireRateLimit(RateLimitPolicies.Checkout)
            .RequireIdempotencyKey(ResaleRules.ReservationTtl)
            .WithName("ReserveListing")
            .Produces<OrderResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        var transfers = v1.MapGroup("/transfers/{id:guid}").WithTags("Resale").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();
        transfers.MapGet("/", GetTransferAsync)
            .WithName("GetTransfer")
            .Produces<TransferInvite>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        transfers.MapPost("/accept", AcceptTransferAsync)
            .WithName("AcceptTransfer")
            .Produces<TransferInvite>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        transfers.MapDelete("/", CancelTransferAsync)
            .WithName("CancelTransfer")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return v1;
    }

    private static async Task<IResult> CreateListingAsync(Guid id, CreateListingRequest request, ClaimsPrincipal user, CreateListingHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(new CreateListingCommand(user.UserId(), id, request.Price), ct)).ToHttpResult();

    private static async Task<IResult> CreateTransferAsync(Guid id, CreateTransferRequest request, ClaimsPrincipal user, CreateTransferHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(new CreateTransferCommand(user.UserId(), id, request.ToEmail), ct)).ToHttpResult();

    private static async Task<IResult> ListAsync(Guid id, ListEventListingsHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(id, ct));

    private static async Task<IResult> CancelListingAsync(Guid id, ClaimsPrincipal user, CancelListingHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> ReserveAsync(Guid id, ClaimsPrincipal user, ReserveListingHandler handler, CancellationToken ct)
    {
        var verified = bool.TryParse(user.FindFirstValue(AuthClaims.EmailVerified), out var value) && value;
        return (await handler.HandleAsync(new ReserveListingCommand(user.UserId(), verified, id), ct)).ToHttpResult();
    }

    private static async Task<IResult> GetTransferAsync(Guid id, ClaimsPrincipal user, GetTransferHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> AcceptTransferAsync(Guid id, ClaimsPrincipal user, AcceptTransferHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> CancelTransferAsync(Guid id, ClaimsPrincipal user, CancelTransferHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();
}
