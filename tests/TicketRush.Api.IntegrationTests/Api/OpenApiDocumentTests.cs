using System.Text.Json;
using System.Text.Json.Nodes;
using TicketRush.Api.IntegrationTests.Infrastructure;

namespace TicketRush.Api.IntegrationTests.Api;

/// <summary>
/// The web app generates its typed client from the committed OpenAPI document (spec 02), so the
/// document must match the api. Run with <c>UPDATE_OPENAPI=1</c> to rewrite it after an api change.
/// </summary>
public sealed class OpenApiDocumentTests(ContainersFixture containers) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        _factory = new ApiFactory(await containers.CreateDatabaseAsync(), containers.RedisConnectionString, environment: "Development");
    }

    public async ValueTask DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Committed_openapi_document_matches_the_api()
    {
        using var client = _factory.CreateClient();
        var live = Normalize(await client.GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken));
        var path = Path.Combine(RepositoryRoot(), "web", "openapi", "ticketrush-api.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, live, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"{path} is missing. Run the tests with UPDATE_OPENAPI=1.");
        var committed = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        committed.ReplaceLineEndings("\n").ShouldBe(live, "OpenAPI document is stale. Run the tests with UPDATE_OPENAPI=1, then `npm run api:gen` in web/.");
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Server URLs depend on the test host, so they are dropped to keep the file stable.</summary>
    private static string Normalize(string json)
    {
        var document = JsonNode.Parse(json)!.AsObject();
        document.Remove("servers");
        return document.ToJsonString(Indented).ReplaceLineEndings("\n") + "\n";
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TicketRush.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
