using System.Buffers.Text;
using System.Text.Json;
using FluentValidation;
using TicketRush.Domain.Events;

namespace TicketRush.Application.Features.Catalog;

public sealed record ListEventsQuery(
    string? Q = null,
    string? City = null,
    string? Category = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    long? PriceMax = null,
    string? Cursor = null,
    int Limit = ListEventsQuery.DefaultLimit)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxSearchLength = 100;

    /// <summary>Blank strings mean "no filter", so they never become distinct cache keys or empty searches.</summary>
    public ListEventsQuery Normalized() => this with
    {
        Q = Clean(Q),
        City = Clean(City),
        Category = Clean(Category),
        Cursor = Clean(Cursor),
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Keyset position: the last item's (StartsAt, Id). Opaque to clients (base64url JSON).</summary>
public sealed record EventCursor(DateTimeOffset StartsAt, Guid Id)
{
    public static string Encode(DateTimeOffset startsAt, Guid id) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new EventCursor(startsAt, id)));

    public static bool TryDecode(string? value, out EventCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value) || !Base64Url.IsValid(value))
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<EventCursor>(Base64Url.DecodeFromChars(value));
            if (decoded is null || decoded.Id == Guid.Empty || decoded.StartsAt == default)
            {
                return false;
            }

            cursor = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class ListEventsValidator : AbstractValidator<ListEventsQuery>
{
    public ListEventsValidator()
    {
        RuleFor(q => q.Q).MaximumLength(ListEventsQuery.MaxSearchLength);
        RuleFor(q => q.City).MaximumLength(100);
        RuleFor(q => q.Category)
            .Must(category => string.IsNullOrWhiteSpace(category) || EventCategories.IsKnown(category.Trim()))
            .WithMessage("Unknown category.");
        RuleFor(q => q.To)
            .GreaterThanOrEqualTo(q => q.From)
            .When(q => q.From is not null && q.To is not null)
            .WithMessage("'to' must not be before 'from'.");
        RuleFor(q => q.PriceMax).GreaterThanOrEqualTo(0);
        RuleFor(q => q.Cursor)
            .Must(cursor => string.IsNullOrWhiteSpace(cursor) || EventCursor.TryDecode(cursor.Trim(), out _))
            .WithMessage("Invalid cursor.");
        RuleFor(q => q.Limit).InclusiveBetween(1, ListEventsQuery.MaxLimit);
    }
}
