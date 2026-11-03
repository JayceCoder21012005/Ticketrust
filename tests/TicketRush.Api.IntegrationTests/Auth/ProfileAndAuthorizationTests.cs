using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TicketRush.Api.IntegrationTests.Infrastructure;
using TicketRush.Domain.Users;

namespace TicketRush.Api.IntegrationTests.Auth;

public sealed class ProfileAndAuthorizationTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private AuthApi _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString);
        _client = AuthApi.CreateClient(_factory);
        _api = new AuthApi(_factory, _client);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Me_requires_a_bearer_token()
    {
        (await _api.GetAsync("/api/v1/me", accessToken: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _api.GetAsync("/api/v1/me", accessToken: "not.a.jwt")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_returns_and_updates_the_profile()
    {
        var login = await _api.SignUpAsync("profile@example.com");

        var me = await Json(await _api.GetAsync("/api/v1/me", login.AccessToken));
        me.GetProperty("fullName").GetString().ShouldBe("Nguyễn An");

        var update = await _api.SendJsonAsync(HttpMethod.Put, "/api/v1/me", new { fullName = "Nguyễn Bình", phoneNumber = "0901234567" }, login.AccessToken);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await Json(await _api.GetAsync("/api/v1/me", login.AccessToken));
        after.GetProperty("fullName").GetString().ShouldBe("Nguyễn Bình");
        after.GetProperty("phoneNumber").GetString().ShouldBe("0901234567");
        after.GetProperty("email").GetString().ShouldBe("profile@example.com");
    }

    [Fact]
    public async Task Update_profile_validates_input()
    {
        var login = await _api.SignUpAsync("invalid-profile@example.com");

        var response = await _api.SendJsonAsync(HttpMethod.Put, "/api/v1/me", new { fullName = "", phoneNumber = "123" }, login.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await Json(response);
        problem.GetProperty("code").GetString().ShouldBe("validation.failed");
        problem.GetProperty("errors").TryGetProperty("fullName", out _).ShouldBeTrue();
        problem.GetProperty("errors").TryGetProperty("phoneNumber", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Avatar_upload_url_accepts_one_image_and_the_profile_only_takes_own_uploads()
    {
        var login = await _api.SignUpAsync("avatar@example.com");
        var ticket = await Json(await _api.SendJsonAsync(HttpMethod.Post, "/api/v1/me/avatar-upload-url", new { contentType = "image/png", size = 4 }, login.AccessToken));
        var uploadUrl = new Uri(ticket.GetProperty("uploadUrl").GetString()!);
        var publicUrl = ticket.GetProperty("publicUrl").GetString()!;
        publicUrl.ShouldStartWith("http://localhost:3000/media/avatars/");

        (await PutAsync(uploadUrl, "image/png", [1, 2, 3, 4])).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var media = await _client.GetAsync(new Uri(new Uri(publicUrl).AbsolutePath, UriKind.Relative), Ct);
        media.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await media.Content.ReadAsByteArrayAsync(Ct)).ShouldBe([1, 2, 3, 4]);

        var own = await _api.SendJsonAsync(HttpMethod.Put, "/api/v1/me", new { fullName = "Avatar", avatarUrl = publicUrl }, login.AccessToken);
        own.StatusCode.ShouldBe(HttpStatusCode.OK);
        var foreign = await _api.SendJsonAsync(HttpMethod.Put, "/api/v1/me", new { fullName = "Avatar", avatarUrl = "https://evil.example/x.png" }, login.AccessToken);
        foreign.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Json(foreign)).GetProperty("code").GetString().ShouldBe("user.avatar_invalid");
    }

    [Theory]
    [InlineData("image/svg+xml", 100)]
    [InlineData("image/png", 2 * 1024 * 1024 + 1)]
    [InlineData("application/pdf", 100)]
    public async Task Avatar_upload_url_only_allows_raster_images_up_to_2MB(string contentType, long size)
    {
        var login = await _api.SignUpAsync($"avatar-{Guid.NewGuid():N}@example.com");

        var response = await _api.SendJsonAsync(HttpMethod.Post, "/api/v1/me/avatar-upload-url", new { contentType, size }, login.AccessToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Upload_rejects_a_different_content_type_an_oversized_body_and_a_tampered_token()
    {
        var login = await _api.SignUpAsync("upload-guard@example.com");
        var ticket = await Json(await _api.SendJsonAsync(HttpMethod.Post, "/api/v1/me/avatar-upload-url", new { contentType = "image/png", size = 4 }, login.AccessToken));
        var uploadUrl = new Uri(ticket.GetProperty("uploadUrl").GetString()!);

        (await PutAsync(uploadUrl, "image/jpeg", [1, 2, 3, 4])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PutAsync(uploadUrl, "image/png", [1, 2, 3, 4, 5])).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var tampered = new Uri(uploadUrl.GetLeftPart(UriPartial.Path) + "?token=forged");
        (await PutAsync(tampered, "image/png", [1, 2, 3, 4])).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Roles.Customer, 403, 403, 403)]
    [InlineData(Roles.Organizer, 200, 403, 403)]
    [InlineData(Roles.Admin, 403, 200, 403)]
    [InlineData(Roles.GateStaff, 403, 403, 200)]
    public async Task Policies_admit_only_their_role(string role, int organizer, int admin, int gate)
    {
        var login = await _api.SignInWithRoleAsync($"{role.ToLowerInvariant()}@example.com", role);

        ((int)(await _api.GetAsync("/_test/auth/organizer", login.AccessToken)).StatusCode).ShouldBe(organizer);
        ((int)(await _api.GetAsync("/_test/auth/admin", login.AccessToken)).StatusCode).ShouldBe(admin);
        ((int)(await _api.GetAsync("/_test/auth/gate", login.AccessToken)).StatusCode).ShouldBe(gate);
        ((int)(await _api.GetAsync("/_test/auth/user", login.AccessToken)).StatusCode).ShouldBe(200);
    }

    [Fact]
    public async Task Access_token_in_the_query_string_is_accepted_only_on_hubs()
    {
        var login = await _api.SignUpAsync("signalr@example.com");
        var query = $"?access_token={Uri.EscapeDataString(login.AccessToken)}";

        var hub = await _client.GetAsync(new Uri("/hubs/_test" + query, UriKind.Relative), Ct);
        hub.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await hub.Content.ReadAsStringAsync(Ct)).ShouldBe(login.Body.GetProperty("user").GetProperty("id").GetString());

        (await _client.GetAsync(new Uri("/_test/auth/user" + query, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _client.GetAsync(new Uri("/api/v1/me" + query, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private Task<HttpResponseMessage> PutAsync(Uri url, string contentType, byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return _client.PutAsync(new Uri(url.PathAndQuery, UriKind.Relative), content, Ct);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
}
