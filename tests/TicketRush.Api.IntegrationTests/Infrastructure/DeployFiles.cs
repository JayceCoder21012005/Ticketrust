namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Finds files under the repo's <c>deploy/</c> folder from the test output folder.</summary>
public static class DeployFiles
{
    public static string Caddyfile() => RepoFiles.Read("deploy", "Caddyfile");
}
