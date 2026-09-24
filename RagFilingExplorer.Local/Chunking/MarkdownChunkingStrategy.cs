using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// The original pipeline: markitdown converts the whole filing to Markdown, SectionSplitter finds the
/// "PART ... &gt; Item ..." sections, and TokenChunker packs each section into ~MaxTokensPerChunk chunks,
/// splitting oversized Markdown tables with their header and row-group labels repeated.
/// </summary>
internal sealed class MarkdownChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        string raw = await MarkItDownConverter.ConvertAsync(filing);
        List<DocumentSection> sections = SectionSplitter.Split(raw);

        List<FilingChunk> chunks = new();
        foreach (DocumentSection section in sections)
        {
            foreach ((string content, int tokens) in TokenChunker.Chunk(section.Body, tokenizer, maxTokensPerChunk, overlapTokens))
            {
                chunks.Add(new FilingChunk(filing.Name, section.Heading, content, tokens));
            }
        }

        return new ChunkedFiling(sections, chunks);
    }
}
