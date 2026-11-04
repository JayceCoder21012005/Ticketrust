using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Auth;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Organizer;

/// <summary>Calls the spec 05 organizer api as one signed-in user.</summary>
public sealed class OrganizerApi(HttpClient client, string accessToken)
{
    public static readonly DateTimeOffset Start = DateTimeOffset.UtcNow.AddDays(30).Date.AddHours(12.5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<OrganizerApi> SignInAsync(ApiFactory factory, HttpClient client, string role = "Organizer")
    {
        var login = await new AuthApi(factory, client).SignInWithRoleAsync($"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com", role);
        return new OrganizerApi(client, login.AccessToken);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"/api/v1/organizer{path}");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    public async Task<JsonElement> VenueAsync(string name = "Nhà hát Thử")
    {
        var response = await PostAsync("/venues", new { name, address = "1 Lê Lợi", city = "Huế", mapUrl = (string?)null, capacity = 500 });
        return await BodyAsync(response, 201);
    }

    /// <summary>Creates a draft at a fresh venue of this organizer and returns its body.</summary>
    public async Task<JsonElement> CreateDraftAsync(string title = "Đêm nhạc thử nghiệm", Guid? venueId = null)
    {
        venueId ??= (await VenueAsync()).GetProperty("id").GetGuid();
        var response = await PostAsync("/events", new { title, category = "concert", venueId, startsAt = Start, endsAt = Start.AddHours(3) });
        return await BodyAsync(response, 201);
    }

    public static async Task<JsonElement> BodyAsync(HttpResponseMessage response, int status = 200)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        ((int)response.StatusCode).ShouldBe(status, text);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        return JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}
