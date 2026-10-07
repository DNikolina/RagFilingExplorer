using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// The default strategy (how it was built, step by step: docs/Decision-Log.md, "XBRL hybrid (v2)"). The filing is
/// parsed once as a DOM (no markitdown), its inline XBRL read before anything else, and the page read into typed
/// blocks, sections and chunks - the block model in <see cref="Structured"/> - rather than written out as text for
/// v1's splitter and chunker to parse back. The heading and packing rules are v1's, shared. From the filing's own
/// tags: a profile built from the cover facts is the first section, "Cover Page" (<see cref="FilingProfile"/>); the
/// primary statements' tables are labelled from the filer's taxonomy (<see cref="StatementLabels"/>), and each
/// chunk's statement type comes from the table it holds; each note to the financial statements is a section headed
/// by its topic (<see cref="NoteTopics"/>); a roll-forward row states its own period, named by the filer's fiscal
/// calendar (<see cref="PeriodLabels"/>); and every chunk's embedding text opens with the company and filing
/// (<see cref="CoverFacts.EmbeddingContext"/>).
/// </summary>
internal sealed class StructuredChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        StructuredFiling read = Read(filing);
        string? context = CoverFacts.EmbeddingContext(read.Xbrl);
        return Task.FromResult(new ChunkedFiling(
            read.Sections.Select(s => new DocumentSection(s.Heading, string.Join("\n\n", s.Blocks.Select(b => b.Text)))).ToList(),
            read.Chunks.Select(c => new FilingChunk(filing.Name, c.Heading, c.Content, c.Tokens, c.StatementType, context)).ToList()));
    }

    public StructuredFiling Read(FileInfo filing)
    {
        IHtmlDocument document = new HtmlParser().ParseDocument(MarkItDownConverter.ReadFiling(filing));

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
