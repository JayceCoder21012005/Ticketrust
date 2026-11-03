using NpgsqlTypes;

namespace TicketRush.Infrastructure.Catalog;

/// <summary>SQL functions created by the AddCatalog migration, mapped in <c>AppDbContext</c>.</summary>
public static class CatalogDbFunctions
{
    /// <summary>
    /// <c>catalog_matches(search_vector, search_text, query)</c>: full-text match on the unaccented query, or a
    /// trigram word-similarity match for typos. Only translatable to SQL.
    /// </summary>
    public static bool Matches(NpgsqlTsVector searchVector, string searchText, string query) =>
        throw new NotSupportedException("Only usable in EF Core queries.");
}
