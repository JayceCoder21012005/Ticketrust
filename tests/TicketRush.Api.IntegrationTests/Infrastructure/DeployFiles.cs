namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Finds files under the repo's <c>deploy/</c> folder from the test output folder.</summary>
public static class DeployFiles
{
    public static string Caddyfile() => ReadFromRepo(Path.Combine("deploy", "Caddyfile"));

    private static string ReadFromRepo(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path))
            {
                return File.ReadAllText(path);
            }
        }

        throw new FileNotFoundException($"{relativePath} not found above the test output folder.");
    }
}
