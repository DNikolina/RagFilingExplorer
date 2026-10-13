namespace RagFilingExplorer.Core.VectorStore;

/// <summary>
/// A chunking strategy's index on disk: rag.&lt;strategy&gt;.db (rag.&lt;strategy&gt;.&lt;model&gt;.db for an embedding
/// model other than nomic-embed-text - EmbeddingTemplates.IndexSuffix; .&lt;n&gt;tok before .db for a chunk budget other
/// than <see cref="UnnamedChunkBudget"/>), its SQLite side files, and the build manifest next to it. Each strategy,
/// embedding model and chunk budget has its own, so switching any of them back and forth never forces a re-embed - see
/// IChunkingStrategy.
/// </summary>
internal sealed record IndexFiles(string DbPath)
{
    public string ManifestPath => DbPath + ".manifest.json";

    public string DbFileName => Path.GetFileName(DbPath);

    public bool Exists => File.Exists(DbPath);

    /// <summary>The chunk budget an index's name leaves out - the size every index was built at before the budget was
    /// named, so those keep their names. A naming rule, not a default: the budget itself is Chunking:MaxTokensPerChunk.</summary>
    public const int UnnamedChunkBudget = 500;

    public static IndexFiles For(DirectoryInfo repoRoot, string strategyName, EmbeddingTemplates embedding, int maxTokensPerChunk) =>
        new(Path.Combine(repoRoot.FullName, string.Join('.', new[] { "rag", strategyName, embedding.IndexSuffix, ChunkBudgetSuffix(maxTokensPerChunk), "db" }.OfType<string>())));

    /// <summary>"1000tok" for a 1,000-token budget, none for <see cref="UnnamedChunkBudget"/> - also names chunk dumps and rank files.</summary>
    public static string? ChunkBudgetSuffix(int maxTokensPerChunk) => maxTokensPerChunk == UnnamedChunkBudget ? null : $"{maxTokensPerChunk}tok";

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
