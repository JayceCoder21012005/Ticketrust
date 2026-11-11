using System.Text.Json;

namespace TicketRush.Infrastructure.Notifications;

/// <summary>
/// The stored form of an email model: a flat JSON object of camelCase string members. Checking the shape when the row is written
/// means a delivery that could never render fails its caller, instead of sitting in the table and failing ten times.
/// </summary>
internal static class EmailModelJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <exception cref="ArgumentException">The model is not a flat object of string members.</exception>
    public static string Serialize(object model)
    {
        var json = JsonSerializer.Serialize(model, Options);
        using var document = JsonDocument.Parse(json);
        var flat = document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().All(member => member.Value.ValueKind == JsonValueKind.String);
        return flat ? json : throw new ArgumentException("An email model must be a flat object of string members.", nameof(model));
    }

    public static IReadOnlyDictionary<string, string> Deserialize(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? throw new JsonException("The stored email model is empty.");
}
