namespace RagFilingExplorer.Local;

/// <summary>
/// Locates the repo root (where data/, chunk-review/ and the rag.&lt;strategy&gt;.db indexes live) by walking
/// up from a starting directory until it finds RagFilingExplorer.slnx - not from the working directory:
/// `dotnet run --project RagFilingExplorer.Local` (the README's command) keeps the caller's working directory,
/// so a path relative to it depends on where the app was launched from.
/// </summary>
internal static class RepoPaths
{
    private const string MarkerFileName = "RagFilingExplorer.slnx";

    public static DirectoryInfo FindRoot(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, MarkerFileName)))
            {
                return directory;
            }
        }

        throw new StartupException(
            $"Could not find the repo root ({MarkerFileName}) above '{startDirectory}'. The app expects to run "
            + "from its build output inside the repo (e.g. via `dotnet run --project RagFilingExplorer.Local`).");
    }
}
