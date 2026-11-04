using System.Globalization;
using System.Text;

namespace TicketRush.Application.Features.Organizer;

public static class OrganizerText
{
    public const int MaxSlugLength = 80;

    /// <summary>"Đêm nhạc Sơn Tùng" → "dem nhac son tung": the folding Postgres' unaccent + lower applies to search text.</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(c is 'đ' or 'Đ' ? 'd' : char.ToLowerInvariant(c));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Lower-case ASCII words joined by '-', at most <see cref="MaxSlugLength"/> characters.</summary>
    public static string Slug(string title)
    {
        var builder = new StringBuilder();
        foreach (var c in Fold(title))
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug.Length == 0 ? "su-kien" : slug;
    }
}
