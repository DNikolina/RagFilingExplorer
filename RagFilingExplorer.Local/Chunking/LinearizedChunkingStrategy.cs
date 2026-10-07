using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// Tables become self-contained row lines before markitdown ever sees them: each table
/// <see cref="HtmlTableLinearizer"/> can linearize is replaced by a &lt;pre&gt; <see cref="RowBlock"/>,
/// which markitdown passes through as a fenced block; tables it can't (a few -
/// exhibit indexes, one small NDAQ table) stay HTML and reach TokenChunker as Markdown tables, exactly as in
/// the Markdown strategy. Everything else - sections, prose chunking, statement-type tagging - is shared.
///
/// Why from the HTML: markitdown discards colspan (most of a filing's tables use it), which is what left the
/// Markdown strategy guessing which row holds the periods and which value sits under which year. See
/// docs/Decision-Log.md, "linearized tables as a second chunking strategy", for the spike's numbers.
/// </summary>
internal sealed class LinearizedChunkingStrategy(Tokenizer tokenizer, int maxTokensPerChunk, int overlapTokens) : IChunkingStrategy
{
    public async Task<ChunkedFiling> ChunkAsync(FileInfo filing)
    {
        string html = MarkItDownConverter.StripIxHeader(MarkItDownConverter.ReadFiling(filing));
        string raw = await MarkItDownConverter.ConvertHtmlAsync(LinearizeTables(html), filing.Name);
        return MarkdownChunkingStrategy.ChunkText(raw, filing.Name, tokenizer, maxTokensPerChunk, overlapTokens);
    }

    /// <summary>Replaces every linearizable top-level table with its row block; returns the rewritten HTML.</summary>
    internal static string LinearizeTables(string html)
    {
        IHtmlDocument document = new HtmlParser().ParseDocument(html);

        // Top-level only: a nested table's text is part of its outer table's cell.
        List<IHtmlTableElement> tables = document.QuerySelectorAll("table").OfType<IHtmlTableElement>()
            .Where(t => t.ParentElement?.Closest("table") is null)
            .ToList();

        foreach (IHtmlTableElement table in tables)
        {
            LinearizedTable result = HtmlTableLinearizer.Linearize(table);
            if (result.Kind is not (LinearizedTableKind.Financial or LinearizedTableKind.Text))
            {
                continue;
            }

            RowBlock block = HtmlTableLinearizer.ToRowBlock(result);
            if (block.Rows.Count == 0)
            {
                continue;
            }

            IElement pre = document.CreateElement("pre");
            pre.TextContent = block.Format();
            table.Replace(pre);
        }

        return document.DocumentElement.OuterHtml;
    }
}
