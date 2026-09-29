using AngleSharp.Html.Dom;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// The Structured strategy's document model (v2; docs/Decision-Log.md, "XBRL hybrid (v2)", "block model"). The
/// filing is read once from the DOM into typed blocks, so what the DOM knows - that a table is one table, which
/// XBRL facts it holds, where it sits - stays attached to it instead of being written into the text and parsed
/// back out. v1's pipeline (markitdown text -> SectionSplitter -> TokenChunker) keeps only strings.
/// </summary>
internal abstract record FilingBlock
{
    /// <summary>The text this block contributes to a chunk.</summary>
    public abstract string Text { get; }
}

/// <summary>A paragraph: one block element's text, lines kept (a &lt;br&gt; is a line break).</summary>
internal sealed record TextBlock(string Paragraph) : FilingBlock
{
    public override string Text => Paragraph;
}

/// <summary>
/// A top-level table, linearized: <see cref="Rows"/> is what a chunk carries; <see cref="Table"/> the
/// linearizer's rows and values, each tagged value with its XBRL context and concept; <see cref="Element"/> the
/// table in the page, for its facts.
/// </summary>
internal sealed record TableBlock(RowBlock Rows, LinearizedTable Table, IHtmlTableElement Element) : FilingBlock
{
    public override string Text => Rows.Text;
}

/// <summary>A run of blocks under one heading path ("PART II &gt; Item 8. Financial Statements ...").</summary>
internal sealed record StructuredSection(string Heading, IReadOnlyList<FilingBlock> Blocks);

/// <summary>A chunk and the blocks it was built from (in order; a paragraph carried as overlap is in two chunks).</summary>
internal sealed record StructuredChunk(string Heading, string Content, int Tokens, IReadOnlyList<FilingBlock> Blocks);

/// <summary>One filing read into sections and chunked, with its inline XBRL.</summary>
internal sealed record StructuredFiling(XbrlDocument Xbrl, IReadOnlyList<StructuredSection> Sections, IReadOnlyList<StructuredChunk> Chunks);
