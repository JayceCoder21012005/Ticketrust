using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Admin;
using TicketRush.Application.Features.Admin;
using TicketRush.Domain.Events;
using TicketRush.Domain.Users;
using TicketRush.Infrastructure.Identity;

namespace TicketRush.Api.Features.Admin;

/// <summary>Every admin change carries a reason; it lands in the audit entry (spec 16).</summary>
public sealed record ReasonRequest(string? Reason);

public sealed record RoleRequest(string? Role, string? Reason);

public sealed record RefundRequest(string? Reason, string? Confirm);

public sealed record OrganizerApplicationRequest(string? CompanyName, string? Phone, string? Description);

public sealed record AdminListRequest(
    [property: FromQuery(Name = "q")] string? Q,
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "limit")] int? Limit);

public sealed record AuditListRequest(
    [property: FromQuery(Name = "actor")] Guid? Actor,
    [property: FromQuery(Name = "entityType")] string? EntityType,
    [property: FromQuery(Name = "entityId")] string? EntityId,
    [property: FromQuery(Name = "action")] string? Action,
    [property: FromQuery(Name = "from")] DateTimeOffset? From,
    [property: FromQuery(Name = "to")] DateTimeOffset? To,
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "limit")] int? Limit);

/// <summary>
/// The admin area (spec 16), policy <c>RequireAdmin</c>. Anyone else, signed in or not, gets 404 on every route
/// (<see cref="AdminNotFoundResultHandler"/>), so the area cannot be discovered.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapPost("/organizer-applications", SubmitApplicationAsync).WithName("SubmitOrganizerApplication").WithTags("Organizer applications")
            .RequireAuthorization().AddEndpointFilter<NoStoreFilter>()
            .Produces<OrganizerApplicationView>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        v1.MapGet("/me/organizer-application", GetMyApplicationAsync).WithName("GetMyOrganizerApplication").WithTags("Organizer applications")
            .RequireAuthorization().AddEndpointFilter<NoStoreFilter>()
            .Produces<OrganizerApplicationView>().ProducesProblem(StatusCodes.Status404NotFound);

        var admin = v1.MapGroup(AdminNotFoundResultHandler.Prefix["/api/v1".Length..]).WithTags("Admin")
            .RequireAuthorization(AuthPolicies.RequireAdmin).AddEndpointFilter<NoStoreFilter>();

        admin.MapGet("/users", ([AsParameters] AdminListRequest r, SearchUsersHandler h, CancellationToken ct) => Run(h.HandleAsync(r.Q, r.Cursor, r.Limit, ct)))
            .WithName("AdminSearchUsers").Produces<AdminPage<AdminUser>>();
        admin.MapGet("/users/{id:guid}", (Guid id, GetAdminUserHandler h, CancellationToken ct) => Run(h.HandleAsync(id, ct)))
            .WithName("AdminGetUser").Produces<AdminUserDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        admin.MapPost("/users/{id:guid}/lock", (Guid id, ReasonRequest r, ClaimsPrincipal u, ChangeUserHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ChangeUserCommand(u.UserId(), id, UserChange.Lock, null, r.Reason), ct))).WithName("AdminLockUser").WithChangeResponses<AdminUser>();
        admin.MapPost("/users/{id:guid}/unlock", (Guid id, ReasonRequest r, ClaimsPrincipal u, ChangeUserHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ChangeUserCommand(u.UserId(), id, UserChange.Unlock, null, r.Reason), ct))).WithName("AdminUnlockUser").WithChangeResponses<AdminUser>();
        admin.MapPost("/users/{id:guid}/roles", (Guid id, RoleRequest r, ClaimsPrincipal u, ChangeUserHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ChangeUserCommand(u.UserId(), id, UserChange.Grant, r.Role, r.Reason), ct))).WithName("AdminGrantRole").WithChangeResponses<AdminUser>();
        admin.MapPost("/users/{id:guid}/roles/revoke", (Guid id, RoleRequest r, ClaimsPrincipal u, ChangeUserHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ChangeUserCommand(u.UserId(), id, UserChange.Revoke, r.Role, r.Reason), ct))).WithName("AdminRevokeRole").WithChangeResponses<AdminUser>();

        admin.MapGet("/organizer-applications", ([FromQuery] OrganizerApplicationStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit, ListOrganizerApplicationsHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(status, cursor, limit, ct))).WithName("AdminListOrganizerApplications").Produces<AdminPage<OrganizerApplicationView>>();
        admin.MapPost("/organizer-applications/{id:guid}/approve", (Guid id, ReasonRequest r, ClaimsPrincipal u, DecideOrganizerApplicationHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new DecideApplicationCommand(u.UserId(), id, true, r.Reason), ct))).WithName("AdminApproveOrganizerApplication").WithChangeResponses<OrganizerApplicationView>();
        admin.MapPost("/organizer-applications/{id:guid}/reject", (Guid id, ReasonRequest r, ClaimsPrincipal u, DecideOrganizerApplicationHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new DecideApplicationCommand(u.UserId(), id, false, r.Reason), ct))).WithName("AdminRejectOrganizerApplication").WithChangeResponses<OrganizerApplicationView>();

        admin.MapGet("/events", ([FromQuery] string? q, [FromQuery] EventStatus? status, [FromQuery] string? cursor, [FromQuery] int? limit, ListAdminEventsHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(q, status, cursor, limit, ct))).WithName("AdminListEvents").Produces<AdminPage<AdminEventRow>>();
        admin.MapPost("/events/{id:guid}/suspend", (Guid id, ReasonRequest r, ModerateEventHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ModerateEventCommand(id, true, r.Reason), ct))).WithName("AdminSuspendEvent").WithChangeResponses<AdminEventRow>();
        admin.MapPost("/events/{id:guid}/restore", (Guid id, ReasonRequest r, ModerateEventHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ModerateEventCommand(id, false, r.Reason), ct))).WithName("AdminRestoreEvent").WithChangeResponses<AdminEventRow>();

        admin.MapGet("/orders", ([AsParameters] AdminListRequest r, SearchAdminOrdersHandler h, CancellationToken ct) => Run(h.HandleAsync(r.Q, r.Cursor, r.Limit, ct)))
            .WithName("AdminSearchOrders").Produces<AdminPage<AdminOrderRow>>();
        admin.MapGet("/orders/{id:guid}", (Guid id, GetAdminOrderHandler h, CancellationToken ct) => Run(h.HandleAsync(id, ct)))
            .WithName("AdminGetOrder").Produces<AdminOrderDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        admin.MapPost("/orders/{id:guid}/refund", (Guid id, RefundRequest r, AdminRefundHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new AdminRefundCommand(id, r.Reason, r.Confirm), ct))).WithName("AdminRefundOrder").WithChangeResponses<AdminOrderDetail>();

        admin.MapGet("/outbox/dead-letters", ([FromQuery] string? cursor, [FromQuery] int? limit, ListDeadLettersHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(cursor, limit, ct))).WithName("AdminListDeadLetters").Produces<AdminPage<DeadLetter>>();
        admin.MapPost("/outbox/{id:guid}/retry", (Guid id, ReasonRequest r, ResolveDeadLetterHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new DeadLetterCommand(id, true, r.Reason), ct))).WithName("AdminRetryDeadLetter").WithChangeResponses();
        admin.MapPost("/outbox/{id:guid}/discard", (Guid id, ReasonRequest r, ResolveDeadLetterHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new DeadLetterCommand(id, false, r.Reason), ct))).WithName("AdminDiscardDeadLetter").WithChangeResponses();

        admin.MapGet("/flags", ([FromQuery] bool? open, [FromQuery] string? cursor, [FromQuery] int? limit, ListFlagsHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(open ?? true, cursor, limit, ct))).WithName("AdminListFlags").Produces<AdminPage<AdminFlagRow>>();
        admin.MapPost("/flags/{id:guid}/dismiss", (Guid id, ReasonRequest r, ClaimsPrincipal u, ResolveFlagHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ResolveFlagCommand(u.UserId(), id, false, r.Reason), ct))).WithName("AdminDismissFlag").WithChangeResponses<AdminFlagRow>();
        admin.MapPost("/flags/{id:guid}/lock", (Guid id, ReasonRequest r, ClaimsPrincipal u, ResolveFlagHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new ResolveFlagCommand(u.UserId(), id, true, r.Reason), ct))).WithName("AdminLockFlaggedUser").WithChangeResponses<AdminFlagRow>();

        admin.MapGet("/audit", ([AsParameters] AuditListRequest r, ListAuditHandler h, CancellationToken ct) =>
            Run(h.HandleAsync(new AuditQuery(r.Actor, r.EntityType, r.EntityId, r.Action, r.From, r.To, r.Cursor, r.Limit), ct))).WithName("AdminListAudit").Produces<AdminPage<AuditRow>>();
        admin.MapGet("/audit/{id:guid}", (Guid id, GetAuditEntryHandler h, CancellationToken ct) => Run(h.HandleAsync(id, ct)))
            .WithName("AdminGetAuditEntry").Produces<AuditRow>().ProducesProblem(StatusCodes.Status404NotFound);
        return v1;
    }

    private static async Task<IResult> Run<T>(Task<Domain.Primitives.Result<T>> work) => (await work).ToHttpResult();

    private static async Task<IResult> Run(Task<Domain.Primitives.Result> work) => (await work).ToHttpResult();

    private static RouteHandlerBuilder WithChangeResponses<T>(this RouteHandlerBuilder route) =>
        route.Produces<T>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

    private static RouteHandlerBuilder WithChangeResponses(this RouteHandlerBuilder route) =>
        route.Produces(StatusCodes.Status204NoContent).ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> SubmitApplicationAsync(OrganizerApplicationRequest request, ClaimsPrincipal user, SubmitOrganizerApplicationHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(new SubmitApplicationCommand(user.UserId(), user.IsInRole(Roles.Organizer), request.CompanyName, request.Phone, request.Description), ct)).ToHttpResult();

    private static async Task<IResult> GetMyApplicationAsync(ClaimsPrincipal user, GetMyOrganizerApplicationHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(user.UserId(), ct)).ToHttpResult();
}

/// <summary>
/// Turns a failed authorization on <c>/api/v1/admin</c> into 404 (spec 16: non-admins must not learn the area exists); every other
/// route keeps the default 401/403.
/// </summary>
public sealed class AdminNotFoundResultHandler : IAuthorizationMiddlewareResultHandler
{
    public const string Prefix = "/api/v1/admin";

    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && context.Request.Path.StartsWithSegments(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            await Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found", extensions: new Dictionary<string, object?> { ["code"] = "not_found" })
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
