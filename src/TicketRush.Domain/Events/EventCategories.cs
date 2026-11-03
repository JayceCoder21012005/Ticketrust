namespace TicketRush.Domain.Events;

public sealed record EventCategory(string Slug, string Name);

/// <summary>Fixed category list (spec 04 <c>/categories</c>). Slugs are stored on events and used in URLs.</summary>
public static class EventCategories
{
    public static readonly IReadOnlyList<EventCategory> All =
    [
        new("concert", "Nhạc sống"),
        new("festival", "Lễ hội"),
        new("theater", "Sân khấu"),
        new("sports", "Thể thao"),
        new("comedy", "Hài kịch"),
        new("workshop", "Hội thảo"),
    ];

    public static bool IsKnown(string slug) => All.Any(category => string.Equals(category.Slug, slug, StringComparison.Ordinal));
}
