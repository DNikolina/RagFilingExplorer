using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// The Structured strategy's replacement for the markitdown CLI: turns a parsed filing into the text shape
/// SectionSplitter and TokenChunker already read, so step 1a changes only who converts the prose (see
/// docs/Decision-Log.md, "XBRL hybrid (v2)"). That shape, read off markitdown's output for all four filings:
/// one paragraph per block element, separated by blank lines (SEC filing agents put each visual line in its own
/// &lt;div&gt;/&lt;p&gt;, and SectionSplitter finds "PART I" / "Item 1. Business" as whole lines); whitespace
/// collapsed but non-breaking spaces kept; links as [text](url); &lt;pre&gt; as a fenced block (a RowBlock);
/// a table the linearizer left alone as a pipe table whose first row is the header, with a merged cell
/// followed by one empty cell per extra column it spans - markitdown's own layout, which TokenChunker's table
/// handling was written against. No headings or bold exist in markitdown's output for these filings (their
/// filing agents style &lt;span&gt;s instead of using &lt;b&gt; or &lt;h1&gt;-&lt;h6&gt;), so none are produced.
/// </summary>
internal static partial class HtmlTextConverter
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "center", "address",
        "blockquote", "figure", "figcaption", "form", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "dl", "dt", "dd",
    };

    // Collapses runs of ordinary whitespace only: [^\S ] leaves non-breaking spaces alone, as markitdown does.
    [GeneratedRegex(@"[^\S ]+")]
    private static partial Regex WhitespaceRunRegex();

    public static string Convert(IDocument document)
    {
        Writer writer = new();
        if (document.Body is not null)
        {
            writer.WriteChildren(document.Body);
        }

        return writer.Finish();
    }

    private sealed class Writer
    {
        private readonly StringBuilder output = new();
        private readonly StringBuilder paragraph = new();

        public string Finish()
        {
            FlushParagraph();
            return output.ToString().TrimEnd() + "\n";
        }

        public void WriteChildren(INode node)
        {
            foreach (INode child in node.ChildNodes)
            {
                Write(child);
            }
        }

        private void Write(INode node)
        {
            if (node is IText text)
            {
                AppendInline(Escape(Collapse(text.Data)));
                return;
            }

            if (node is not IElement element)
            {
                return;
            }

            switch (element.LocalName)
            {
                case "script" or "style" or "head" or "title":
                    return;
                case "br":
                    paragraph.Append('\n');
                    return;
                case "hr":
                    WriteBlock("---");
                    return;
                case "pre":
                    WriteBlock($"{RowBlock.Fence}\n{element.TextContent.Trim('\n')}\n{RowBlock.Fence}");
                    return;
                case "table" when element is IHtmlTableElement table:
                    WriteBlock(PipeTable(table));
                    return;
                case "a" when element.GetAttribute("href") is { Length: > 0 } href:
                    string label = InlineText(element);
                    AppendInline(label.Length == 0 ? string.Empty : $"[{label}]({href})");
                    return;
                case "img":
                    AppendInline($"![{element.GetAttribute("alt") ?? string.Empty}]({element.GetAttribute("src") ?? string.Empty})");
                    return;
                case "li":
                    FlushParagraph();
                    paragraph.Append(element.ParentElement?.LocalName == "ol" ? $"{element.Index() + 1}. " : "* ");
                    WriteChildren(element);
                    FlushParagraph();
                    return;
            }

            if (BlockTags.Contains(element.LocalName))
            {
                FlushParagraph();
                WriteChildren(element);
                FlushParagraph();
                return;
            }

            WriteChildren(element);
        }

        private void AppendInline(string text)
        {
            // Adjacent inline runs keep one space between them, however many the source had across nodes.
            if (paragraph.Length > 0 && text.StartsWith(' ') && paragraph[^1] is ' ' or '\n')
            {
                text = text.TrimStart(' ');
            }

            paragraph.Append(text);
        }

        private void WriteBlock(string block)
        {
            FlushParagraph();
            if (block.Trim().Length > 0)
            {
                output.Append(block.Trim()).Append("\n\n");
            }
        }

        private void FlushParagraph()
        {
            string lines = string.Join('\n', paragraph.ToString().Split('\n').Select(l => l.Trim(' ')).Where(l => l.Length > 0));
            paragraph.Clear();
            if (lines.Length > 0)
            {
                output.Append(lines).Append("\n\n");
            }
        }
    }

    // markitdown's table layout (markdownify's): every row "| cell | cell |", each merged cell followed by an
    // empty " |" for every extra column it covers, and a "| --- |" separator (one per column, spans counted)
    // under the header row. A first row of <th> cells is the header; otherwise - every table in these filings -
    // an empty header row is made up and the separator goes above the first real row.
    private static string PipeTable(IHtmlTableElement table)
    {
        List<IHtmlTableRowElement> rows = table.Rows.Where(r => r.ParentElement?.Closest("table") == table).ToList();
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        int columns = rows[0].Cells.Sum(c => Math.Max(1, c.ColumnSpan));
        string separator = "|" + string.Concat(Enumerable.Repeat(" --- |", columns)) + "\n";
        bool firstRowIsHeader = rows[0].Cells.Length > 0 && rows[0].Cells.All(c => c.LocalName == "th");

        StringBuilder text = new();
        if (!firstRowIsHeader)
        {
            text.Append('|').Append(string.Concat(Enumerable.Repeat("  |", columns))).Append('\n').Append(separator);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            StringBuilder row = new("|");
            foreach (IHtmlTableCellElement cell in rows[i].Cells)
            {
                row.Append(' ').Append(InlineText(cell)).Append(string.Concat(Enumerable.Repeat(" |", Math.Max(1, cell.ColumnSpan))));
            }

            text.Append(row).Append('\n');
            if (i == 0 && firstRowIsHeader)
            {
                text.Append(separator);
            }
        }

        return text.ToString();
    }

    // A cell's or link's text on one line: its own links kept as [text](url), line breaks as spaces.
    private static string InlineText(IElement element)
    {
        StringBuilder text = new();
        foreach (INode node in element.Descendants())
        {
            // Text inside a nested link is written by that link below; the element's own text (a link's
            // label, when the element is the link) is written here.
            if (node is IText t && (t.ParentElement?.Closest("a[href]") is not { } link || link == element))
            {
                text.Append(Escape(Collapse(t.Data)));
            }
            else if (node is IElement { LocalName: "a" } a && a != element && a.GetAttribute("href") is { Length: > 0 } href && a.TextContent.Trim().Length > 0)
            {
                text.Append($"[{Escape(Collapse(a.TextContent).Trim())}]({href})");
            }
            else if (node is IElement e && (e.LocalName == "br" || BlockTags.Contains(e.LocalName)))
            {
                // A line break or a block inside a cell ("Exhibit<div>Number</div>") separates words.
                text.Append(' ');
            }
        }

        return Collapse(text.ToString()).Trim(' ');
    }

    private static string Collapse(string text) => WhitespaceRunRegex().Replace(text, " ");

    // markitdown escapes the Markdown emphasis characters in text ("\*" for a footnote asterisk).
    private static string Escape(string text) => text.Replace("*", "\\*").Replace("_", "\\_");
}
