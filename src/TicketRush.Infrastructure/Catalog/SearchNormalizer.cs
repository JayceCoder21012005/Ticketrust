using System.Globalization;
using System.Text;

namespace TicketRush.Infrastructure.Catalog;

/// <summary>
/// The C# twin of <c>lower(immutable_unaccent(...))</c> used by the generated search columns (spec 04): lower case, combining
/// marks removed, đ → d. Lets the suggestion queries pass the normalised text as a constant the GIN indexes can use.
/// </summary>
public static class SearchNormalizer
{
    public static string Normalize(string value)
    {
        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c == 'đ' ? 'd' : c);
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
