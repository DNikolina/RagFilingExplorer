using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// v2's strategy, built up in measured steps (docs/Decision-Log.md, "XBRL hybrid (v2)"). The filing is parsed
/// once as a DOM (step 1a - no markitdown), its inline XBRL read before anything else (1b-i), and the page read
/// into typed blocks, sections and chunks - the block model in <see cref="Structured"/> - rather than written
/// out as text for v1's splitter and chunker to parse back. The heading and packing rules are v1's, shared.
/// Step 1b-ii: a profile built from the tagged cover facts is the first section, "Cover Page"
/// (<see cref="FilingProfile"/>). Step 1b-iii-a: the primary statements' tables are labelled from the filer's
/// taxonomy (<see cref="StatementLabels"/>), and each chunk's statement type comes from the table it holds.
/// Step 1b-iii-b: each note to the financial statements is a section headed by its topic (<see cref="NoteTopics"/>).
/// Step 1d: every chunk's embedding text opens with the company and filing (<see cref="CoverFacts.EmbeddingContext"/>).
/// </summary>
internal sealed class StructuredChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        StructuredFiling read = await ReadAsync(filing);
        string? context = CoverFacts.EmbeddingContext(read.Xbrl);
        return new ChunkedFiling(
            read.Sections.Select(s => new DocumentSection(s.Heading, string.Join("\n\n", s.Blocks.Select(b => b.Text)))).ToList(),
            read.Chunks.Select(c => new FilingChunk(filing.Name, c.Heading, c.Content, c.Tokens, c.StatementType, context)).ToList());
    }

    public async Task<StructuredFiling> ReadAsync(FileInfo filing)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filing.FullName);
        IHtmlDocument document = new HtmlParser().ParseDocument(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes));

        // The taxonomy sits next to the filing in data/, downloaded with it from EDGAR (docs/Decision-Log.md).
        FileInfo schema = TaxonomyReader.FindForFiling(document, filing.Directory!)
            ?? throw new InvalidOperationException($"{filing.Name}: no taxonomy schema (.xsd) in {filing.DirectoryName} for the namespaces this page declares.");
        return Read(document, TaxonomyReader.Read(schema));
    }

    public StructuredFiling Read(IHtmlDocument document, XbrlTaxonomy taxonomy)
    {
        XbrlDocument xbrl = InlineXbrlReader.Read(document);
        List<FilingBlock> blocks = FilingBlockReader.Read(document, NoteTopics.Find(document, xbrl, taxonomy), xbrl);
        List<StructuredSection> sections = StructuredSections.Split(StatementLabels.Label(blocks, taxonomy));
        if (FilingProfile.Build(xbrl) is { } profile)
        {
            sections.Insert(0, new StructuredSection(FilingProfile.Heading, [new TextBlock(profile)]));
        }

        List<StructuredChunk> chunks = sections
            .SelectMany(s => StructuredChunker.Chunk(s, tokenizer, maxTokensPerChunk, overlapTokens))
            .ToList();
        return new StructuredFiling(xbrl, sections, chunks);
    }
}
