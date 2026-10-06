using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Turns one filing into sections and chunks - the only part of the pipeline that differs between
/// strategies. Everything downstream (statement-type tagging in FilingChunkRecords, embedding, retrieval) only
/// sees the resulting <see cref="FilingChunk"/>s, so strategies can be swapped via Chunking:Strategy in
/// appsettings.json. Each strategy builds its own index (rag.&lt;strategy&gt;.db) and chunk dumps
/// (chunk-review/&lt;strategy&gt;/), so switching back and forth never forces a re-embed.
/// </summary>
internal interface IChunkingStrategy
{
    Task<ChunkedFiling> ChunkAsync(FileInfo filing);
}

internal sealed record ChunkedFiling(List<DocumentSection> Sections, List<FilingChunk> Chunks);

/// <summary>The values Chunking:Strategy accepts. Bound as an enum, so a typo fails at startup.</summary>
internal enum ChunkingStrategyKind
{
    /// <summary>markitdown → SectionSplitter → TokenChunker over Markdown tables (the original pipeline).</summary>
    Markdown,

    /// <summary>Tables linearized from the HTML into self-contained row lines first, then the same pipeline.</summary>
    Linearized,

    /// <summary>The filing read as a DOM and its inline XBRL, no markitdown (see StructuredChunkingStrategy).</summary>
    Structured,
}

internal static class ChunkingStrategies
{
    public static IChunkingStrategy Create(ChunkingSettings settings) => settings.Strategy switch
    {
        ChunkingStrategyKind.Markdown => new MarkdownChunkingStrategy(
            TiktokenTokenizer.CreateForModel(settings.TokenizerModel), settings.MaxTokensPerChunk, settings.OverlapTokens),
        ChunkingStrategyKind.Linearized => new LinearizedChunkingStrategy(
            TiktokenTokenizer.CreateForModel(settings.TokenizerModel), settings.MaxTokensPerChunk, settings.OverlapTokens),
        ChunkingStrategyKind.Structured => new StructuredChunkingStrategy(
            TiktokenTokenizer.CreateForModel(settings.TokenizerModel), settings.MaxTokensPerChunk, settings.OverlapTokens),
        _ => throw new ArgumentOutOfRangeException(nameof(settings), settings.Strategy, "Unknown chunking strategy."),
    };

    /// <summary>Lowercase name used in the strategy's index and chunk-dump paths, e.g. "markdown".</summary>
    public static string FileName(ChunkingStrategyKind strategy) => strategy.ToString().ToLowerInvariant();
}
