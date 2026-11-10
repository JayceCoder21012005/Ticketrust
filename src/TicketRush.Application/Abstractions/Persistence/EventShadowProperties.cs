namespace TicketRush.Application.Abstractions.Persistence;

/// <summary>Shadow properties of <c>Event</c> that use cases read through <c>EF.Property</c>.</summary>
public static class EventShadowProperties
{
    /// <summary>Postgres' <c>xmin</c> as an optimistic concurrency token; clients echo it back as <c>version</c>.</summary>
    public const string Version = "Version";

    /// <summary>Generated, unaccented lower-case title + lineup + city (spec 04).</summary>
    public const string SearchText = "SearchText";
}

/// <summary>Shadow properties of <c>TicketTier</c>. Only <c>ITierInventory</c> writes the sold count, with a conditional UPDATE.</summary>
public static class TicketTierShadowProperties
{
    /// <summary>General-admission tickets paid for so far (column <c>sold_count</c>); never above the tier's capacity.</summary>
    public const string SoldCount = "SoldCount";
}
