namespace TicketRush.Application.Features.Catalog;

public sealed record CityCount(string City, int Events);

public sealed record CategoryCount(string Category, int Events);

/// <summary>The home's sections (UI refresh), picked on the server so the page stays static and the ranking stays private.</summary>
public sealed record HomeSections(
    IReadOnlyList<EventSummary> Hot,
    IReadOnlyList<EventSummary> OpeningSoon,
    IReadOnlyList<EventSummary> ThisWeek,
    IReadOnlyList<EventSummary> Upcoming,
    IReadOnlyList<CityCount> Cities,
    IReadOnlyList<CategoryCount> Categories);

/// <summary>A published, not ended event with the signals the hot ranking needs.</summary>
/// <param name="RecentSold">Tickets issued in the last 24 hours.</param>
/// <param name="Sold">Tickets issued so far.</param>
/// <param name="Capacity">Seats plus general-admission capacity.</param>
public sealed record HomeCandidate(EventSummary Summary, int RecentSold, int Sold, int Capacity, int Followers);

/// <summary>How hot an event is: momentum first (3 a ticket sold in the last 24 h), then how full it is (1 a percent), then interest (½ a follower).</summary>
public static class HotScore
{
    public static double Of(int recentSold, int sold, int capacity, int followers) =>
        (recentSold * 3.0) + (capacity > 0 ? 100.0 * sold / capacity : 0) + (followers * 0.5);

    public static double Of(HomeCandidate candidate) =>
        Of(candidate.RecentSold, candidate.Sold, candidate.Capacity, candidate.Followers);
}

public static class HomeSectionPicker
{
    public const int HotCount = 4;
    public const int OpeningSoonCount = 4;
    public const int ThisWeekCount = 6;
    public const int UpcomingCount = 6;

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public static HomeSections Pick(IReadOnlyList<HomeCandidate> candidates, DateTimeOffset now)
    {
        var summaries = candidates.Select(c => c.Summary).OrderBy(e => e.StartsAt).ThenBy(e => e.Id).ToList();

        // Each event appears once, in the first section that wants it: opening soon (it keeps its countdown there), hot, this
        // week, upcoming. A repeated card is noise for the buyer and weight for the page.
        var shown = new HashSet<Guid>();
        List<EventSummary> Take(IEnumerable<EventSummary> source, int count)
        {
            var picked = source.Where(e => !shown.Contains(e.Id)).Take(count).ToList();
            shown.UnionWith(picked.Select(e => e.Id));
            return picked;
        }

        var openingSoon = Take(summaries.Where(e => e.NextSaleOpensAt is not null && !e.IsOnSale && !e.IsSoldOut).OrderBy(e => e.NextSaleOpensAt), OpeningSoonCount);
        var hot = Take(
            candidates
                .Where(c => !c.Summary.IsSoldOut && (c.Summary.IsOnSale || c.Summary.NextSaleOpensAt <= now + Week))
                .OrderByDescending(HotScore.Of)
                .ThenBy(c => c.Summary.StartsAt)
                .Select(c => c.Summary),
            HotCount);
        var thisWeek = Take(summaries.Where(e => e.StartsAt <= now + Week), ThisWeekCount);
        var upcoming = Take(summaries.Where(e => e.StartsAt > now + Week), UpcomingCount);

        var cities = summaries.GroupBy(e => e.City).Select(g => new CityCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Events).ThenBy(c => c.City, StringComparer.Ordinal).ToList();
        var categories = summaries.GroupBy(e => e.Category).Select(g => new CategoryCount(g.Key, g.Count()))
            .OrderByDescending(c => c.Events).ThenBy(c => c.Category, StringComparer.Ordinal).ToList();

        return new HomeSections(hot, openingSoon, thisWeek, upcoming, cities, categories);
    }
}

public sealed class GetHomeHandler(IEventCatalogReader reader)
{
    public Task<HomeSections> HandleAsync(CancellationToken ct) => reader.GetHomeAsync(ct);
}
