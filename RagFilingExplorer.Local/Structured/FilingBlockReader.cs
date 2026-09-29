using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Reads a parsed filing into <see cref="FilingBlock"/>s in reading order - the Structured strategy's replacement
/// for the markitdown CLI (step 1a) and, since the block model, for writing the page out as one string: one
/// <see cref="TextBlock"/> per block element (SEC filing agents put each visual line in its own
/// &lt;div&gt;/&lt;p&gt;, and the heading rules find "PART I" / "Item 1. Business" as whole lines), whitespace
/// collapsed but non-breaking spaces kept, links as [text](url); one <see cref="TableBlock"/> per top-level table.
/// The text keeps markitdown's shape (docs/Decision-Log.md, step 1a), which the heading and packing rules were
/// written against. No headings or bold are produced: these filers style &lt;span&gt;s instead of using
/// &lt;b&gt; or &lt;h1&gt;-&lt;h6&gt;. The hidden inline-XBRL header is skipped - read it first
/// (<see cref="Xbrl.InlineXbrlReader"/>).
/// </summary>
internal static partial class FilingBlockReader
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "header", "footer", "main", "nav", "aside", "center", "address",
        "blockquote", "figure", "figcaption", "form", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "dl", "dt", "dd", "pre",
    };

    // Collapses runs of whitespace other than non-breaking spaces, which markitdown keeps. Written as an escape:
    // the step-1a converter had a literal U+00A0 here, which reads as a space and was lost when the code was
    // copied into this reader - four spaces then survived in ORCL's cover-page lines.
    [GeneratedRegex(@"[^\S ]+")]
    private static partial Regex WhitespaceRunRegex();

    /// <param name="notes">Where the notes sit (<see cref="NoteTopics"/>): each block read inside one gets its topic.</param>
    /// <param name="contexts">The filing's XBRL contexts: a roll-forward row gets its period (<see cref="PeriodLabels"/>).</param>
    public static List<FilingBlock> Read(IDocument document, IReadOnlyList<NoteSpan>? notes = null, IReadOnlyDictionary<string, XbrlContext>? contexts = null)
    {
        Reader reader = new(notes ?? [], contexts);
        if (document.Body is not null)
        {
            reader.ReadChildren(document.Body);
        }

        return JoinGapsToNextNote(reader.Finish());
    }

    /// <summary>
    /// A block between two notes belongs to the next one. NDAQ and NFLX tag a note from its title, leaving its number
    /// outside ("2." + "SUMMARY OF SIGNIFICANT ACCOUNTING"), so the heading paragraph began outside the note and split
    /// off as a section of its own - 32 of them; the other gaps are page furniture (a "Table of Contents" link, a lone
    /// non-breaking space) at a page break before the next note. Measured 2026-09-29: every gap between two notes in
    /// the four filings is one of the two.
    /// </summary>
    private static List<FilingBlock> JoinGapsToNextNote(List<FilingBlock> blocks)
    {
        int first = blocks.FindIndex(b => b.Topic is not null);
        int last = blocks.FindLastIndex(b => b.Topic is not null);

        // Likewise the Notes' own title ("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS" - where the section rules
        // already start a section) and what follows it up to the first note - Note 1's number, a date: on its own it
        // was an 8-20 token title-only chunk in every filing, the kind that took top-5 slots for statement questions.
        // Only across paragraphs: a table between them would mean the title isn't the one right before the notes.
        int notesTitle = first < 0 ? -1 : blocks.FindLastIndex(first, first + 1,
            b => b is TextBlock t && t.Paragraph.Split('\n').Any(StatementTypeDetector.IsNotesToFinancialStatementsBoundary));
        if (notesTitle >= 0 && blocks.Skip(notesTitle).Take(first - notesTitle).All(b => b is TextBlock))
        {
            first = notesTitle - 1;
        }

        string? next = null;
        for (int i = last; i > first; i--)
        {
            next = blocks[i].Topic ?? next;
            if (blocks[i].Topic is null)
            {
                blocks[i] = blocks[i] with { Topic = next };
            }
        }

        return blocks;
    }

    /// <summary>
    /// A top-level table as a row block, or null for a table with no text (decorative spacers). A table the
    /// financial path can't linearize becomes text rows (step 1c's first change): MSFT's exhibit index is seven
    /// tables, one per page; the two holding management-contract exhibits ("10.6*") read as financial - a text
    /// label beside a number ("10.4", the referenced exhibit) - and fell back ("two values in row '10.6*' map to
    /// one column", "cell text lost: 'Filed Herewith'") to pipe tables that were mostly empty cells, while the
    /// other five pages came out as text rows. Same for all 5 fallbacks of 371.
    /// </summary>
    internal static TableBlock? ReadTable(IHtmlTableElement table, IReadOnlyDictionary<string, XbrlContext>? contexts = null)
    {
        LinearizedTable result = HtmlTableLinearizer.Linearize(table);
        if (result.Kind == LinearizedTableKind.Fallback)
        {
            result = HtmlTableLinearizer.LinearizeAsText(table);
        }
        else if (result.Kind == LinearizedTableKind.Financial && contexts is not null)
        {
            result = PeriodLabels.Apply(result, contexts);
        }

        RowBlock? rows = result.Kind switch
        {
            LinearizedTableKind.Financial => HtmlTableLinearizer.ToRowBlock(result),
            LinearizedTableKind.Text => TextRowBlock(result, HtmlTableLinearizer.LeadingBoldRowCount(table)),
            _ => null,
        };
        rows = rows is null ? null : Normalize(rows);
        if (rows is null || rows.Rows.Count == 0)
        {
            // Every table with text linearizes to rows; one that didn't would lose its text silently.
            return table.TextContent.Trim().Length == 0 ? null
                : throw new InvalidOperationException($"A table with text produced no rows: '{Shorten(table.TextContent)}'.");
        }

        return new TableBlock(rows, result, table);
    }

    /// <summary>
    /// A text table's column-name rows go in the row block's context line, which the chunker repeats on every
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

    // One row per line, trailing whitespace off, no empty rows: the shape a row block had after its round trip
    // through the page text (RowBlock.TryParse), which the chunk dumps were built from.
    private static RowBlock Normalize(RowBlock block) => new(
        string.IsNullOrWhiteSpace(block.Context) ? null : block.Context.Trim(),
        block.Rows.SelectMany(r => r.Split('\n')).Select(r => r.TrimEnd()).Where(r => r.Length > 0).ToList());

    private static string Shorten(string text)
    {
        string collapsed = WhitespaceRunRegex().Replace(text, " ").Trim();
        return collapsed.Length > 60 ? collapsed[..60] + "..." : collapsed;
    }

    private sealed class Reader(IReadOnlyList<NoteSpan> notes, IReadOnlyDictionary<string, XbrlContext>? contexts)
    {
        private readonly List<FilingBlock> blocks = new();
        private readonly StringBuilder paragraph = new();
        private readonly Dictionary<IElement, string> noteStarts = notes.ToDictionary(n => n.Start, n => n.Topic);
        private readonly HashSet<IElement> noteEnds = notes.Select(n => n.End).ToHashSet();

        // The note being read (from its start element through the end of its end element), and the one the
        // current paragraph began in - a paragraph belongs where its first text is.
        private string? topic;
        private string? paragraphTopic;
        private bool paragraphHasText;

        public List<FilingBlock> Finish()
        {
            FlushParagraph();
            return blocks;
        }

        public void ReadChildren(INode node)
        {
            foreach (INode child in node.ChildNodes)
            {
                Read(child);
            }
        }

        private void Read(INode node)
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

            if (noteStarts.TryGetValue(element, out string? startsNote))
            {
                topic = startsNote;
            }

            ReadElement(element);

            if (noteEnds.Contains(element))
            {
                topic = null;
            }
        }

        private void ReadElement(IElement element)
        {
            switch (element.LocalName)
            {
                case "script" or "style" or "head" or "title" or "ix:header":
                    return;
                case "br":
                    paragraph.Append('\n');
                    return;
                case "hr":
                    FlushParagraph();
                    blocks.Add(new TextBlock("---") { Topic = topic });
                    return;
                case "table" when element is IHtmlTableElement table:
                    FlushParagraph();
                    if (ReadTable(table, contexts) is { } block)
                    {
                        blocks.Add(block with { Topic = topic });
                    }

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
                    paragraphTopic = topic;
                    paragraphHasText = true;
                    paragraph.Append(element.ParentElement?.LocalName == "ol" ? $"{element.Index() + 1}. " : "* ");
                    ReadChildren(element);
                    FlushParagraph();
                    return;
            }

            if (BlockTags.Contains(element.LocalName))
            {
                FlushParagraph();
                ReadChildren(element);
                FlushParagraph();
                return;
            }

            ReadChildren(element);
        }

        private void AppendInline(string text)
        {
            // Adjacent inline runs keep one space between them, however many the source had across nodes.
            if (paragraph.Length > 0 && text.StartsWith(' ') && paragraph[^1] is ' ' or '\n')
            {
                text = text.TrimStart(' ');
            }

            // A paragraph's topic is where its first visible text is: a note's first element can open mid-paragraph.
            if (!paragraphHasText && text.Trim().Length > 0)
            {
                paragraphTopic = topic;
                paragraphHasText = true;
            }

            paragraph.Append(text);
        }

        private void FlushParagraph()
        {
            string lines = string.Join('\n', paragraph.ToString().Split('\n').Select(l => l.Trim(' ')).Where(l => l.Length > 0));
            paragraph.Clear();
            paragraphHasText = false;
            if (lines.Length > 0)
            {
                blocks.Add(new TextBlock(lines) { Topic = paragraphTopic });
            }
        }
    }

    // A link's text on one line: nested links kept as [text](url), line breaks and blocks as spaces.
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
                text.Append(' ');
            }
        }

        return Collapse(text.ToString()).Trim(' ');
    }

    private static string Collapse(string text) => WhitespaceRunRegex().Replace(text, " ");

    // markitdown escapes the Markdown emphasis characters in text ("\*" for a footnote asterisk).
    private static string Escape(string text) => text.Replace("*", "\\*").Replace("_", "\\_");
}
