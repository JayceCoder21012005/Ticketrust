using System.Security.Claims;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Waitlist;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Features.Waitlist;

/// <param name="TierId">A specific tier, or null for any tier.</param>
/// <param name="Quantity">1 to 4 tickets.</param>
public sealed record JoinWaitlistRequest(Guid? TierId, int Quantity);

/// <summary>Sold-out waitlist (spec 13). Every route reads or changes only the caller's own entries.</summary>
public static class WaitlistEndpoints
{
    public static RouteGroupBuilder MapWaitlistEndpoints(this RouteGroupBuilder v1)
    {
        var mine = v1.MapGroup("/events/{id:guid}/waitlist").WithTags("Waitlist").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();
        mine.MapPost("/", JoinAsync)
            .WithName("JoinWaitlist")
            .Produces<WaitlistStatusResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        mine.MapGet("/me", GetMineAsync)
            .WithName("GetMyWaitlistEntry")
            .Produces<WaitlistStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        mine.MapDelete("/me", LeaveAsync)
            .WithName("LeaveWaitlist")
            .Produces(StatusCodes.Status204NoContent);

        v1.MapGet("/me/waitlist", ListMineAsync)
            .WithName("ListMyWaitlist")
            .WithTags("Waitlist")
            .RequireAuthorization()
            .AddEndpointFilter<NoStoreFilter>()
            .Produces<IReadOnlyList<MyWaitlistItem>>();

        var offers = v1.MapGroup("/waitlist/offers/{offerId:guid}").WithTags("Waitlist").RequireAuthorization().AddEndpointFilter<NoStoreFilter>();
        offers.MapGet("/", GetOfferAsync)
            .WithName("GetWaitlistOffer")
            .Produces<WaitlistOfferResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        offers.MapPost("/accept", AcceptAsync)
            .WithName("AcceptWaitlistOffer")
            .Produces<AcceptedWaitlistOffer>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return v1;
    }

    private static async Task<IResult> JoinAsync(Guid id, JoinWaitlistRequest request, ClaimsPrincipal user, JoinWaitlistHandler handler, CancellationToken ct)
    {
        var verified = bool.TryParse(user.FindFirstValue(AuthClaims.EmailVerified), out var value) && value;
        return (await handler.HandleAsync(new JoinWaitlistCommand(user.UserId(), verified, id, request.TierId, request.Quantity), ct)).ToHttpResult();
    }

    private static async Task<IResult> GetMineAsync(Guid id, ClaimsPrincipal user, GetMyWaitlistEntryHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> LeaveAsync(Guid id, ClaimsPrincipal user, LeaveWaitlistHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> ListMineAsync(ClaimsPrincipal user, ListMyWaitlistHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(user.UserId(), ct));

    private static async Task<IResult> GetOfferAsync(Guid offerId, ClaimsPrincipal user, GetWaitlistOfferHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), offerId, ct)).ToHttpResult();

    private static async Task<IResult> AcceptAsync(Guid offerId, ClaimsPrincipal user, AcceptWaitlistOfferHandler handler, CancellationToken ct)
    {
        var verified = bool.TryParse(user.FindFirstValue(AuthClaims.EmailVerified), out var value) && value;
        return (await handler.HandleAsync(user.UserId(), verified, offerId, ct)).ToHttpResult();
    }
}
