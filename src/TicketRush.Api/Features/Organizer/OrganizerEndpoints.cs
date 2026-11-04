using System.Security.Claims;
using FluentValidation;
using TicketRush.Api.Common.Auth;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Abstractions.Storage;
using TicketRush.Application.Features.Organizer;
using TicketRush.Application.Features.Organizer.Events;
using TicketRush.Application.Features.Organizer.Venues;
using TicketRush.Domain.Events;
using TicketRush.Domain.Events.Layout;
using TicketRush.Domain.Events.Publishing;

namespace TicketRush.Api.Features.Organizer;

public sealed record CreateVenueRequest(string Name, string Address, string City, string? MapUrl, int Capacity);

public sealed record CreateEventRequest(string Title, string Category, Guid VenueId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record UpdateEventInfoRequest(string Version, string Title, string Description, IReadOnlyList<string> Lineup, string? CoverImageUrl);

public sealed record UpdateEventScheduleRequest(string Version, string Category, Guid VenueId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record CoverUploadRequest(string ContentType, long Size);

public sealed record ReplaceTiersRequest(string Version, IReadOnlyList<TierInput> Tiers);

/// <summary>The spec 05 layout JSON (also accepted as an import) plus the version being edited.</summary>
public sealed record ReplaceLayoutRequest(string Version, IReadOnlyList<SectionDefinition> Sections);

public sealed record ReplacePhasesRequest(string Version, IReadOnlyList<PhaseInput> Phases);

/// <summary>422 body of <c>POST publish</c>: ProblemDetails plus the failed checklist rules (documents the OpenAPI shape).</summary>
public sealed record PublishChecklistProblem(string Type, string Title, int Status, string Code, IReadOnlyList<PublishFailure> Failures)
{
    public const string ErrorCode = "event.publish_checklist_failed";
}

/// <summary>
/// Spec 05. The spec's <c>PUT /organizer/events/{id}</c> is split into <c>/info</c> (allowed after publishing) and
/// <c>/schedule</c> (draft only), mirroring the wizard and the freeze rule.
/// </summary>
public static class OrganizerEndpoints
{
    public static RouteGroupBuilder MapOrganizerEndpoints(this RouteGroupBuilder v1)
    {
        var organizer = v1.MapGroup("/organizer").WithTags("Organizer").RequireAuthorization(AuthPolicies.RequireOrganizer);

        organizer.MapGet("/venues", ListVenuesAsync).Produces<IReadOnlyList<VenueItem>>();
        organizer.MapPost("/venues", CreateVenueAsync).Produces<VenueItem>(StatusCodes.Status201Created).ProducesValidationProblem();

        organizer.MapGet("/events", ListEventsAsync).Produces<IReadOnlyList<OrganizerEventItem>>();
        organizer.MapPost("/events", CreateEventAsync)
            .Produces<OrganizerEvent>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        organizer.MapGet("/events/{id:guid}", GetEventAsync).Produces<OrganizerEvent>().ProducesProblem(StatusCodes.Status404NotFound);
        organizer.MapPut("/events/{id:guid}/info", UpdateInfoAsync).WithEventChangeResponses();
        organizer.MapPut("/events/{id:guid}/schedule", UpdateScheduleAsync).WithEventChangeResponses();
        organizer.MapPut("/events/{id:guid}/tiers", ReplaceTiersAsync).WithEventChangeResponses();
        organizer.MapPut("/events/{id:guid}/layout", ReplaceLayoutAsync).WithEventChangeResponses();
        organizer.MapPut("/events/{id:guid}/phases", ReplacePhasesAsync).WithEventChangeResponses();
        organizer.MapGet("/events/{id:guid}/checklist", GetChecklistAsync)
            .Produces<PublishChecklistResult>()
            .ProducesProblem(StatusCodes.Status404NotFound);
        organizer.MapPost("/events/{id:guid}/publish", PublishAsync)
            .Produces<OrganizerEvent>()
            .Produces<PublishChecklistProblem>(StatusCodes.Status422UnprocessableEntity, "application/problem+json")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        organizer.MapPost("/events/{id:guid}/cancel", CancelAsync)
            .Produces<OrganizerEvent>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        organizer.MapPost("/events/{id:guid}/cover-upload-url", CreateCoverUploadUrlAsync)
            .Produces<UploadTicket>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);
        return v1;
    }

    /// <summary>Every change of an event: the updated event, or validation / 404 / 409 (stale version or frozen).</summary>
    internal static RouteHandlerBuilder WithEventChangeResponses(this RouteHandlerBuilder builder) => builder
        .Produces<OrganizerEvent>()
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> ListVenuesAsync(ClaimsPrincipal user, ListVenuesHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(user.UserId(), ct));

    private static async Task<IResult> CreateVenueAsync(
        CreateVenueRequest request,
        ClaimsPrincipal user,
        IValidator<CreateVenueCommand> validator,
        CreateVenueHandler handler,
        CancellationToken ct)
    {
        var command = new CreateVenueCommand(user.UserId(), request.Name, request.Address, request.City, request.MapUrl, request.Capacity);
        if (await validator.ValidateToProblemAsync(command, ct) is { } problem)
        {
            return problem;
        }

        var venue = await handler.HandleAsync(command, ct);
        return TypedResults.Created($"/api/v1/organizer/venues/{venue.Id}", venue);
    }

    private static async Task<IResult> ListEventsAsync(
        string? q,
        EventStatus? status,
        ClaimsPrincipal user,
        ListOrganizerEventsHandler handler,
        CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(new ListOrganizerEventsQuery(user.UserId(), q, status), ct));

    private static async Task<IResult> CreateEventAsync(
        CreateEventRequest request,
        ClaimsPrincipal user,
        IValidator<CreateEventDraftCommand> validator,
        CreateEventDraftHandler handler,
        CancellationToken ct)
    {
        var command = new CreateEventDraftCommand(user.UserId(), request.Title, request.Category, request.VenueId, request.StartsAt, request.EndsAt);
        return await validator.ValidateToProblemAsync(command, ct)
            ?? (await handler.HandleAsync(command, ct)).ToHttpResult(evt => TypedResults.Created($"/api/v1/organizer/events/{evt.Id}", evt));
    }

    private static async Task<IResult> GetEventAsync(Guid id, ClaimsPrincipal user, GetOrganizerEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, user.UserId(), ct)).ToHttpResult();

    private static async Task<IResult> UpdateInfoAsync(
        Guid id,
        UpdateEventInfoRequest request,
        ClaimsPrincipal user,
        IValidator<UpdateEventInfoCommand> validator,
        UpdateEventInfoHandler handler,
        CancellationToken ct)
    {
        var cover = string.IsNullOrWhiteSpace(request.CoverImageUrl) ? null : request.CoverImageUrl;
        var command = new UpdateEventInfoCommand(user.UserId(), id, request.Version, request.Title, request.Description ?? string.Empty, request.Lineup ?? [], cover);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> UpdateScheduleAsync(
        Guid id,
        UpdateEventScheduleRequest request,
        ClaimsPrincipal user,
        IValidator<UpdateEventScheduleCommand> validator,
        UpdateEventScheduleHandler handler,
        CancellationToken ct)
    {
        var command = new UpdateEventScheduleCommand(user.UserId(), id, request.Version, request.Category, request.VenueId, request.StartsAt, request.EndsAt);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> ReplaceTiersAsync(
        Guid id,
        ReplaceTiersRequest request,
        ClaimsPrincipal user,
        IValidator<ReplaceTiersCommand> validator,
        ReplaceTiersHandler handler,
        CancellationToken ct)
    {
        var command = new ReplaceTiersCommand(user.UserId(), id, request.Version, request.Tiers ?? []);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> ReplaceLayoutAsync(
        Guid id,
        ReplaceLayoutRequest request,
        ClaimsPrincipal user,
        IValidator<ReplaceLayoutCommand> validator,
        ReplaceLayoutHandler handler,
        CancellationToken ct)
    {
        var command = new ReplaceLayoutCommand(user.UserId(), id, request.Version, new SeatLayoutDefinition(request.Sections ?? []));
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> ReplacePhasesAsync(
        Guid id,
        ReplacePhasesRequest request,
        ClaimsPrincipal user,
        IValidator<ReplacePhasesCommand> validator,
        ReplacePhasesHandler handler,
        CancellationToken ct)
    {
        var command = new ReplacePhasesCommand(user.UserId(), id, request.Version, request.Phases ?? []);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }

    private static async Task<IResult> GetChecklistAsync(Guid id, ClaimsPrincipal user, GetPublishChecklistHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, user.UserId(), ct)).ToHttpResult();

    private static async Task<IResult> PublishAsync(Guid id, ClaimsPrincipal user, PublishEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, user.UserId(), ct)).ToHttpResult(outcome => outcome.Event is { } evt
            ? TypedResults.Ok(evt)
            : TypedResults.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "The event is not ready to publish.",
                type: "https://httpstatuses.io/422",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = PublishChecklistProblem.ErrorCode,
                    ["failures"] = outcome.Failures,
                }));

    private static async Task<IResult> CancelAsync(Guid id, ClaimsPrincipal user, CancelEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, user.UserId(), ct)).ToHttpResult();

    private static async Task<IResult> CreateCoverUploadUrlAsync(
        Guid id,
        CoverUploadRequest request,
        ClaimsPrincipal user,
        IValidator<CoverUploadCommand> validator,
        CoverUploadHandler handler,
        CancellationToken ct)
    {
        var command = new CoverUploadCommand(user.UserId(), id, request.ContentType, request.Size);
        return await validator.ValidateToProblemAsync(command, ct) ?? (await handler.HandleAsync(command, ct)).ToHttpResult();
    }
}
