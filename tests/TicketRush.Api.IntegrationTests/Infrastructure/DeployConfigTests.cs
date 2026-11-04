namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Production reverse proxy (spec 07): SignalR must reach the api directly, not through Next's rewrites.</summary>
public sealed class DeployConfigTests
{
    private static string Caddyfile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "deploy", "Caddyfile");
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException("deploy/Caddyfile not found above the test output folder.");
    }

    [Fact]
    public void Caddy_routes_hubs_straight_to_the_api_before_the_web_app()
    {
        // `next start` in standalone output does not proxy WebSocket upgrades (vercel/next.js#44209).
        var text = Caddyfile();
        var hubs = text.IndexOf("handle /hubs/*", StringComparison.Ordinal);
        var api = text.IndexOf("reverse_proxy api:8080", StringComparison.Ordinal);
        var web = text.IndexOf("reverse_proxy web:3000", StringComparison.Ordinal);

        hubs.ShouldBeGreaterThanOrEqualTo(0);
        api.ShouldBeGreaterThan(hubs);
        web.ShouldBeGreaterThan(api);
    }
}
