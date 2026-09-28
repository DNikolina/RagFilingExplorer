using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// v2's strategy, built up in measured steps (docs/Decision-Log.md, "XBRL hybrid (v2)"). Step 1a: the filing
/// is read as a DOM and converted by <see cref="HtmlTextConverter"/> instead of the markitdown CLI; tables are
/// linearized by the Linearized strategy's <see cref="HtmlTableLinearizer"/>. So 1a differed from Linearized in
/// one thing only - who converts the prose - and matched its chunks (989 of 999 identical). Step 1c's first
/// change: a table the linearizer falls back on becomes text rows (see <see cref="LinearizeTables"/>).
/// Step 1b-ii: the filing's inline XBRL is read before its header is removed, and a profile built from the
/// tagged cover facts becomes the first section, "Cover Page" (<see cref="FilingProfile"/>).
/// Sections, chunking and statement-type tagging are the shared v1 code, unchanged.
/// </summary>
internal sealed class StructuredChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filing.FullName);
        IHtmlDocument document = new HtmlParser().ParseDocument(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes));
        XbrlDocument xbrl = InlineXbrlReader.Read(document);
        List<DocumentSection> sections = SectionSplitter.Split(ConvertToText(document));
        if (FilingProfile.Build(xbrl) is { } profile)
        {
            sections.Insert(0, new DocumentSection(FilingProfile.Heading, profile));
        }

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

    /// <summary>
    /// Decoded filing HTML to the section/chunk input text. The hidden inline-XBRL header is removed as a DOM
    /// element here rather than by regex - it's the part of the filing v2's later steps will read.
    /// </summary>
    internal static string ConvertToText(string html) => ConvertToText(new HtmlParser().ParseDocument(html));

    /// <summary>As <see cref="ConvertToText(string)"/>, on an already-parsed page - read its XBRL first: this removes the header.</summary>
    internal static string ConvertToText(IHtmlDocument document)
    {
        foreach (AngleSharp.Dom.IElement header in document.QuerySelectorAll("*").Where(e => e.LocalName == "ix:header").ToList())
        {
            header.Remove();
        }

        LinearizeTables(document);
        return HtmlTextConverter.Convert(document);
    }

    /// <summary>
    /// As <see cref="LinearizedChunkingStrategy.LinearizeTables"/>, with one difference (step 1c's first change):
    /// a table the financial path can't linearize becomes text rows instead of staying HTML for a pipe table.
    /// MSFT's exhibit index is seven tables, one per page; the two holding management-contract exhibits ("10.6*")
    /// read as financial - a text label beside a number ("10.4", the referenced exhibit) - and fell back ("two
    /// values in row '10.6*' map to one column", "cell text lost: 'Filed Herewith'") to pipe tables that were
    /// mostly empty cells, while the other five pages came out as text rows. Same for all 5 fallbacks of 371.
    /// </summary>
    internal static void LinearizeTables(IHtmlDocument document)
    {
        List<IHtmlTableElement> tables = document.QuerySelectorAll("table").OfType<IHtmlTableElement>()
            .Where(t => t.ParentElement?.Closest("table") is null)
            .ToList();

        foreach (IHtmlTableElement table in tables)
        {
            LinearizedTable result = HtmlTableLinearizer.Linearize(table);
            if (result.Kind == LinearizedTableKind.Fallback)
            {
                result = HtmlTableLinearizer.LinearizeAsText(table);
            }

            if (result.Kind is not (LinearizedTableKind.Financial or LinearizedTableKind.Text))
            {
                continue;
            }

            RowBlock block = result.Kind == LinearizedTableKind.Text ? TextRowBlock(result, HtmlTableLinearizer.LeadingBoldRowCount(table))
                : HtmlTableLinearizer.ToRowBlock(result);
            if (block.Rows.Count == 0)
            {
                continue;
            }

            AngleSharp.Dom.IElement pre = document.CreateElement("pre");
            pre.TextContent = block.Format();
            table.Replace(pre);
        }
    }

    /// <summary>
    /// A text table's column-name rows go in the row block's context line, which TokenChunker repeats on every
    /// piece of a split block (step 1c's second change). MSFT chunk 198 was "4.24 | Description of Securities |
    /// 10-K | 6/30/2024 | 4.26 | 7/30/2024" with no "Exhibit Number | ... | Form | ... | Exhibit" above it.
    /// </summary>
    internal static RowBlock TextRowBlock(LinearizedTable table, int headerRows)
    {
        IReadOnlyList<string> lines = table.TextLines;
        return headerRows > 0 && headerRows < lines.Count
            ? new RowBlock(string.Join(" / ", lines.Take(headerRows)), lines.Skip(headerRows).ToList())
            : new RowBlock(null, lines);
    }
}
