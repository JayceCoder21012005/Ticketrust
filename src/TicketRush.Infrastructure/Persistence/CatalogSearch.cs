namespace TicketRush.Infrastructure.Persistence;

/// <summary>Names of the generated search columns on <c>events</c> (shadow properties, never loaded into aggregates).</summary>
internal static class CatalogSearch
{
    public const string TextProperty = "SearchText";
    public const string VectorProperty = "SearchVector";
}
