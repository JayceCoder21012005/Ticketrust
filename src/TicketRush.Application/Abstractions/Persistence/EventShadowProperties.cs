namespace TicketRush.Application.Abstractions.Persistence;

/// <summary>Shadow properties of <c>Event</c> that use cases read through <c>EF.Property</c>.</summary>
public static class EventShadowProperties
{
    /// <summary>Postgres' <c>xmin</c> as an optimistic concurrency token; clients echo it back as <c>version</c>.</summary>
    public const string Version = "Version";

    /// <summary>Generated, unaccented lower-case title + lineup + city (spec 04).</summary>
    public const string SearchText = "SearchText";
}
