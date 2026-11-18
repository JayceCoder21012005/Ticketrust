using TicketRush.Application.Features.Catalog;

namespace TicketRush.Api.Features.Catalog;

/// <summary>Search-as-you-type (UI refresh). Anonymous, counted under the global rate limit, cached 30 s by browsers and proxies.</summary>
public static class SearchEndpoints
{
    public static RouteGroupBuilder MapSearchEndpoints(this RouteGroupBuilder v1)
    {
        v1.MapGet("/search/suggest", SuggestAsync)
            .WithName("SuggestSearch")
            .WithTags("Catalog")
            .AllowAnonymous()
            .Produces<SearchSuggestions>()
            .ProducesValidationProblem();
        return v1;
    }

    private static async Task<IResult> SuggestAsync(string? q, SuggestHandler handler, HttpContext context, CancellationToken ct)
    {
        if (q is { Length: > SuggestLimits.MaxQueryLength })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = [$"At most {SuggestLimits.MaxQueryLength} characters."],
            });
        }

        context.Response.Headers.CacheControl = "public, max-age=30";
        return TypedResults.Ok(await handler.HandleAsync(q, ct));
    }
}
