using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// v2's strategy, built up in measured steps (docs/Decision-Log.md, "XBRL hybrid (v2)"). Step 1a: the filing
/// is read as a DOM and converted by <see cref="HtmlTextConverter"/> instead of the markitdown CLI; tables are
/// linearized by the Linearized strategy's <see cref="HtmlTableLinearizer"/>. So 1a differed from Linearized in
/// one thing only - who converts the prose - and matched its chunks (989 of 999 identical). Step 1c's first
/// change: a table the linearizer falls back on becomes text rows (see <see cref="LinearizeTables"/>).
/// Sections, chunking and statement-type tagging are the shared v1 code, unchanged.
/// </summary>
internal sealed class StructuredChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filing.FullName);
        string html = MarkItDownConverter.DetectEncoding(bytes).GetString(bytes);
        string text = ConvertToText(html);
        List<DocumentSection> sections = SectionSplitter.Split(text);

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
    internal static string ConvertToText(string html)
    {
        HtmlParser parser = new();
        IHtmlDocument document = parser.ParseDocument(html);
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

            RowBlock block = HtmlTableLinearizer.ToRowBlock(result);
            if (block.Rows.Count == 0)
            {
                continue;
            }

            AngleSharp.Dom.IElement pre = document.CreateElement("pre");
            pre.TextContent = block.Format();
            table.Replace(pre);
        }
    }
}
