using System.Security.Claims;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Follows.FollowEvent;
using TicketRush.Application.Features.Catalog;
using TicketRush.Application.Features.Follows.GetFollowState;
using TicketRush.Application.Features.Follows.ListMyFollows;
using TicketRush.Application.Features.Follows.UnfollowEvent;

namespace TicketRush.Api.Features.Follows;

/// <summary>"Remind me" (spec 09): following a published event, per user. Reads and writes touch only the caller's own row.</summary>
public static class FollowEndpoints
{
    public static RouteGroupBuilder MapFollowEndpoints(this RouteGroupBuilder v1)
    {
        var follow = v1.MapGroup("/events/{id:guid}/follow").WithTags("Follows").RequireAuthorization();

        follow.MapGet("/", GetAsync)
            .WithName("GetFollowState")
            .Produces<FollowState>();
        follow.MapPost("/", FollowAsync)
            .WithName("FollowEvent")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        follow.MapDelete("/", UnfollowAsync)
            .WithName("UnfollowEvent")
            .Produces(StatusCodes.Status204NoContent);

        v1.MapGet("/me/follows", ListMineAsync)
            .WithName("ListMyFollows")
            .WithTags("Follows")
            .RequireAuthorization()
            .AddEndpointFilter<NoStoreFilter>()
            .Produces<IReadOnlyList<EventSummary>>();
        return v1;
    }

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, GetFollowStateHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(user.UserId(), id, ct));

    private static async Task<IResult> ListMineAsync(ClaimsPrincipal user, ListMyFollowsHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(user.UserId(), ct));

    private static async Task<IResult> FollowAsync(Guid id, ClaimsPrincipal user, FollowEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> UnfollowAsync(Guid id, ClaimsPrincipal user, UnfollowEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();
}
