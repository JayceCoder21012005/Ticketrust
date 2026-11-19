namespace TicketRush.Application.Features.Catalog;

public sealed record EventSuggestion(string Slug, string Title, DateTimeOffset StartsAt, string VenueName, string City, string? CoverImageUrl);

public sealed record VenueSuggestion(string Name, string City);

/// <summary>Search-as-you-type answer (UI refresh), grouped so the palette can show events, artists, venues and cities.</summary>
public sealed record SearchSuggestions(
    IReadOnlyList<EventSuggestion> Events,
    IReadOnlyList<string> Artists,
    IReadOnlyList<VenueSuggestion> Venues,
    IReadOnlyList<string> Cities)
{
    public static readonly SearchSuggestions Empty = new([], [], [], []);
}

public static class SuggestLimits
{
    public const int MaxQueryLength = 100;
    public const int Events = 6;
    public const int Artists = 4;
    public const int Venues = 3;
    public const int Cities = 3;

    /// <summary>Below this many letters or digits the palette shows what is hot instead of matches.</summary>
    public const int MinTermLength = 2;
}

public sealed class SuggestHandler(IEventCatalogReader reader)
{
    public async Task<SearchSuggestions> HandleAsync(string? q, CancellationToken ct)
    {
        var query = (q ?? string.Empty).Trim();
        if (query.Count(char.IsLetterOrDigit) < SuggestLimits.MinTermLength)
        {
            var home = await reader.GetHomeAsync(ct);
            return SearchSuggestions.Empty with
            {
                Events = home.Hot.Select(e => new EventSuggestion(e.Slug, e.Title, e.StartsAt, e.VenueName, e.City, e.CoverImageUrl)).ToList(),
            };
        }

        return await reader.SuggestAsync(query, ct);
    }
}
