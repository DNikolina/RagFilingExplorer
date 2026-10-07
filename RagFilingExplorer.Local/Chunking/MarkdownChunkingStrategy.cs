using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// The original pipeline: markitdown converts the whole filing to Markdown, SectionSplitter finds the
/// "PART ... &gt; Item ..." sections, and TokenChunker packs each section into ~MaxTokensPerChunk chunks,
/// splitting oversized Markdown tables with their header and row-group labels repeated.
/// </summary>
internal sealed class MarkdownChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing) =>
        ChunkText(await MarkItDownConverter.ConvertAsync(filing), filing.Name, tokenizer, maxTokensPerChunk, overlapTokens);

    /// <summary>markitdown's text into sections and chunks - shared with the Linearized strategy.</summary>
    internal static ChunkedFiling ChunkText(string raw, string filingName, Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens)
    {
        List<DocumentSection> sections = SectionSplitter.Split(raw);

        List<FilingChunk> chunks = [];
        foreach (DocumentSection section in sections)
        {
            foreach ((string content, int tokens) in TokenChunker.Chunk(section.Body, tokenizer, maxTokensPerChunk, overlapTokens))
            {
                chunks.Add(new FilingChunk(filingName, section.Heading, content, tokens));
            }
        }

        return new ChunkedFiling(sections, chunks);
    }
}
