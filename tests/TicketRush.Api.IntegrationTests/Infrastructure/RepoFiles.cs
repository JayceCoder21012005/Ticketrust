namespace TicketRush.Api.IntegrationTests.Infrastructure;

/// <summary>Reads files that live in the repository (not copied to the test output) by walking up from the output folder.</summary>
public static class RepoFiles
{
    public static string Read(params string[] relativePath)
    {
        var path = Path.Combine(relativePath);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, path);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"{path} not found above the test output folder.");
    }
}
