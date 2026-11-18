using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using TicketRush.Api.Common.Http;
using TicketRush.Application.Features.Catalog;
using TicketRush.Domain.Events;

namespace TicketRush.Api.Features.Catalog;

/// <summary>Query string of <c>GET /events</c>; every filter is optional.</summary>
public sealed record ListEventsRequest(
    [property: FromQuery(Name = "q")] string? Q,
    [property: FromQuery(Name = "city")] string? City,
    [property: FromQuery(Name = "category")] string? Category,
    [property: FromQuery(Name = "from")] DateTimeOffset? From,
    [property: FromQuery(Name = "to")] DateTimeOffset? To,
    [property: FromQuery(Name = "priceMax")] long? PriceMax,
    [property: FromQuery(Name = "cursor")] string? Cursor,
    [property: FromQuery(Name = "limit")] int? Limit);

/// <summary>Public catalog (spec 04). Anonymous; reads are cached by the HybridCache decorator.</summary>
public static class CatalogEndpoints
{
    /// <summary>Seat layouts change only when an organizer edits the event (spec 05), which changes the ETag.</summary>
    private const string SeatMapCacheControl = "public, max-age=3600";

    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder v1)
    {
        var events = v1.MapGroup("/events").WithTags("Catalog").AllowAnonymous();
        events.MapGet("/", ListAsync).WithName("ListEvents").Produces<EventPage>().ProducesValidationProblem();
        events.MapGet("/{slug}", GetAsync).WithName("GetEvent").Produces<EventDetail>().ProducesProblem(StatusCodes.Status404NotFound);
        events.MapGet("/{id:guid}/seat-map", GetSeatMapAsync)
            .WithName("GetSeatMap")
            .Produces<SeatMap>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);
        events.MapGet("/{id:guid}/availability", GetAvailabilityAsync)
            .WithName("GetAvailability")
            .Produces<AvailabilityResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        v1.MapGet("/categories", () => TypedResults.Ok(EventCategories.All)).WithName("ListCategories").WithTags("Catalog").AllowAnonymous();
        v1.MapGet("/catalog/home", GetHomeAsync).WithName("GetHome").WithTags("Catalog").AllowAnonymous().Produces<HomeSections>();
        v1.MapGet("/cities", ListCitiesAsync).WithName("ListCities").WithTags("Catalog").AllowAnonymous();
        return v1;
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListEventsRequest request,
        IValidator<ListEventsQuery> validator,
        ListEventsHandler handler,
        CancellationToken ct)
    {
        var query = new ListEventsQuery(
            request.Q,
            request.City,
            request.Category,
            request.From,
            request.To,
            request.PriceMax,
            request.Cursor,
            request.Limit ?? ListEventsQuery.DefaultLimit);

        return await validator.ValidateToProblemAsync(query, ct) ?? TypedResults.Ok(await handler.HandleAsync(query, ct));
    }

    private static async Task<IResult> GetAsync(string slug, GetEventHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(slug, ct)).ToHttpResult();

    private static async Task<IResult> GetSeatMapAsync(Guid id, HttpContext context, GetSeatMapHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, ct)).ToHttpResult(map =>
        {
            var etag = new EntityTagHeaderValue($"\"{map.Version}\"");
            var headers = context.Response.GetTypedHeaders();
            headers.ETag = etag;
            context.Response.Headers.CacheControl = SeatMapCacheControl;

            var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
            return ifNoneMatch.Any(candidate => candidate.Equals(EntityTagHeaderValue.Any) || candidate.Compare(etag, useStrongComparison: false))
                ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
                : TypedResults.Ok(map);
        });

    private static async Task<IResult> GetAvailabilityAsync(Guid id, GetAvailabilityHandler handler, CancellationToken ct) =>
        (await handler.HandleAsync(id, ct)).ToHttpResult();

    /// <summary>Cached 30 s like the listing; the web's home is an ISR page refreshed on the same beat.</summary>
    private static async Task<IResult> GetHomeAsync(GetHomeHandler handler, HttpContext context, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "public, max-age=30";
        return TypedResults.Ok(await handler.HandleAsync(ct));
    }

    private static async Task<IResult> ListCitiesAsync(ListCitiesHandler handler, CancellationToken ct) =>
        TypedResults.Ok(await handler.HandleAsync(ct));
}
