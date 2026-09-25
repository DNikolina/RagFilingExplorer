namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// A chunking strategy's index on disk: rag.&lt;strategy&gt;.db, its SQLite side files, and the build
/// manifest next to it. Each chunking strategy has its own, so switching Chunking:Strategy back and forth
/// never forces a re-embed - see IChunkingStrategy.
/// </summary>
internal sealed record IndexFiles(string DbPath)
{
    public string ManifestPath => DbPath + ".manifest.json";

    public string DbFileName => Path.GetFileName(DbPath);

    public bool Exists => File.Exists(DbPath);

    public static IndexFiles For(DirectoryInfo repoRoot, string strategyName) =>
        new(Path.Combine(repoRoot.FullName, $"rag.{strategyName}.db"));

    // An existing index is only trusted if it has a manifest (written only after a build finishes) that
    // matches the current settings and filings - see IndexManifest for the two failure modes this closes.
    public void EnsureCurrent(IndexManifest currentManifest)
    {
        IndexManifest? builtManifest = IndexManifest.TryLoad(ManifestPath);
        if (builtManifest is null)
        {
            throw new StartupException(
                $"{DbPath} exists but has no build manifest ({Path.GetFileName(ManifestPath)}), so it can't be "
                + "trusted as complete - an earlier build was interrupted or failed, or it predates manifests. "
                + "Run with --rebuild.");
        }

        List<string> differences = builtManifest.DescribeDifferences(currentManifest);
        if (differences.Count > 0)
        {
            throw new StartupException(
                $"{DbPath} is out of date:\n"
                + string.Concat(differences.Select(d => $"  - {d}\n"))
                + "Run with --rebuild to rebuild it (this re-embeds every chunk and takes several minutes).");
        }
    }

    // --rebuild forces a fresh chunk+embed cycle - the only way to pick up a chunking/embedding *code*
    // change (settings and filing changes are detected automatically via the manifest).
    public void Delete()
    {
        foreach (string path in new[] { DbPath, DbPath + "-shm", DbPath + "-wal", ManifestPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
