namespace RagFilingExplorer.Local;

/// <summary>
/// Locates the repo root (where data/, chunk-review/ and the rag.&lt;strategy&gt;.db indexes live) by walking
/// up from a starting directory until it finds RagFilingExplorer.slnx. Paths used to be resolved as
/// "current working directory + ..", which only worked when the app was launched from inside
/// RagFilingExplorer.Local/ - `dotnet run --project RagFilingExplorer.Local` from the repo root (the
/// README's own documented command) keeps the caller's working directory, confirmed with a throwaway
/// probe app, so it looked for data/ one level *above* the repo instead.
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
