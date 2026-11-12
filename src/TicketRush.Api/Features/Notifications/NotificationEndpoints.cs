using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Notifications.ListNotifications;
using TicketRush.Application.Features.Notifications.MarkAllNotificationsRead;
using TicketRush.Application.Features.Notifications.MarkNotificationRead;

namespace TicketRush.Api.Features.Notifications;

/// <summary>Query string of <c>GET /me/notifications</c>.</summary>
public sealed record ListNotificationsRequest(
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "limit")] int? Limit);

/// <summary>The in-app inbox (spec 09): the keyset list with its unread count, and marking notices read.</summary>
public static class NotificationEndpoints
{
    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder v1)
    {
        var notifications = v1.MapGroup("/me/notifications").WithTags("Notifications").RequireAuthorization();

        notifications.MapGet("/", ListAsync)
            .WithName("ListMyNotifications")
            .Produces<NotificationPage>()
            .ProducesValidationProblem();
        notifications.MapPost("/{id:guid}/read", MarkReadAsync)
            .WithName("MarkNotificationRead")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
        notifications.MapPost("/read-all", MarkAllReadAsync)
            .WithName("MarkAllNotificationsRead")
            .Produces(StatusCodes.Status204NoContent);
        return v1;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListNotificationsRequest request,
        ClaimsPrincipal user,
        IValidator<ListNotificationsQuery> validator,
        ListNotificationsHandler handler,
        CancellationToken ct)
    {
        var query = new ListNotificationsQuery(user.UserId(), request.Cursor, request.Limit ?? ListNotificationsQuery.DefaultLimit);
        return await validator.ValidateToProblemAsync(query, ct) ?? TypedResults.Ok(await handler.HandleAsync(query, ct));
    }

    private static async Task<IResult> MarkReadAsync(Guid id, ClaimsPrincipal user, MarkNotificationReadHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), id, ct)).ToHttpResult();

    private static async Task<IResult> MarkAllReadAsync(ClaimsPrincipal user, MarkAllNotificationsReadHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), ct)).ToHttpResult();
}
