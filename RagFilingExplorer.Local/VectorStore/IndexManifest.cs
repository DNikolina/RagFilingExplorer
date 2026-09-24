using System.Security.Cryptography;
using System.Text.Json;

namespace RagFilingExplorer.Local.VectorStore;

/// <summary>
/// Records what a finished rag.&lt;strategy&gt;.db was built from: the embedding model, the chunking
/// strategy and settings, and a SHA-256 hash of every filing. Written next to the index only after
/// every chunk has been upserted, so it doubles as a completion marker.
///
/// It closes two gaps in the old "rag.db exists => skip the build" check:
/// - An interrupted or failed build (Ctrl+C during the ~10-minute embedding pass, markitdown missing,
///   Ollama going away) left a partial rag.db behind - SqliteVectorStore creates the file before any
///   chunking starts - which every later run then trusted as complete.
/// - Changing EmbeddingModel, chunk size/overlap, or the filings in data/ silently reused the old index.
///   An embedding-model change is the worst case: query vectors from the new model get compared against
///   stored vectors from the old one, so retrieval returns noise with no error at all.
///
/// Changes to chunking/embedding *code* aren't detectable this way - those still need --rebuild.
/// </summary>
internal sealed class IndexManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public required string EmbeddingModel { get; init; }

    // Each strategy has its own index file, so this normally can't differ - recorded anyway so an index
    // renamed or copied by hand is caught rather than silently answering from the wrong chunks.
    public required string ChunkingStrategy { get; init; }
    public required string TokenizerModel { get; init; }
    public required int MaxTokensPerChunk { get; init; }
    public required int OverlapTokens { get; init; }

    /// <summary>Filing file name -> lowercase hex SHA-256 of its bytes.</summary>
    public required SortedDictionary<string, string> FilingHashes { get; init; }

    public static IndexManifest Create(AppSettings settings, IEnumerable<FileInfo> filings) => new()
    {
        EmbeddingModel = settings.Ollama.EmbeddingModel,
        ChunkingStrategy = settings.Chunking.Strategy.ToString(),
        TokenizerModel = settings.Chunking.TokenizerModel,
        MaxTokensPerChunk = settings.Chunking.MaxTokensPerChunk,
        OverlapTokens = settings.Chunking.OverlapTokens,
        FilingHashes = new SortedDictionary<string, string>(
            filings.ToDictionary(f => f.Name, f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.FullName)))),
            StringComparer.Ordinal),
    };

    /// <summary>Returns null if the file doesn't exist or can't be parsed (treated the same as missing).</summary>
    public static IndexManifest? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IndexManifest>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>
    /// Human-readable reasons this (stored) manifest doesn't match <paramref name="current"/> - empty if
    /// the index is still valid for the current settings and filings.
    /// </summary>
    public List<string> DescribeDifferences(IndexManifest current)
    {
        List<string> differences = new();

        void Compare<T>(string name, T built, T now)
        {
            if (!EqualityComparer<T>.Default.Equals(built, now))
            {
                differences.Add($"{name} changed: index built with '{built}', settings now say '{now}'");
            }
        }

        Compare("Ollama:EmbeddingModel", EmbeddingModel, current.EmbeddingModel);
        Compare("Chunking:Strategy", ChunkingStrategy, current.ChunkingStrategy);
        Compare("Chunking:TokenizerModel", TokenizerModel, current.TokenizerModel);
        Compare("Chunking:MaxTokensPerChunk", MaxTokensPerChunk, current.MaxTokensPerChunk);
        Compare("Chunking:OverlapTokens", OverlapTokens, current.OverlapTokens);

        foreach (string name in current.FilingHashes.Keys.Except(FilingHashes.Keys))
        {
            differences.Add($"data/{name} was added since the index was built");
        }

        foreach (string name in FilingHashes.Keys.Except(current.FilingHashes.Keys))
        {
            differences.Add($"data/{name} was removed since the index was built");
        }

        foreach ((string name, string hash) in current.FilingHashes)
        {
            if (FilingHashes.TryGetValue(name, out string? builtHash) && builtHash != hash)
            {
                differences.Add($"data/{name} changed since the index was built");
            }
        }

        return differences;
    }
}
