using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Reads a parsed filing into <see cref="FilingBlock"/>s in reading order - the Structured strategy's replacement
/// for the markitdown CLI: one
/// <see cref="TextBlock"/> per block element (SEC filing agents put each visual line in its own
/// &lt;div&gt;/&lt;p&gt;, and the heading rules find "PART I" / "Item 1. Business" as whole lines), whitespace
/// collapsed but non-breaking spaces kept, links as [text](url); one <see cref="TableBlock"/> per top-level table.
/// The exception is a fixed page layout, where every printed line is a positioned &lt;div&gt; and a paragraph is only
/// visible as line spacing: those lines are joined back into paragraphs (<see cref="PositionedLine"/>).
/// The text keeps markitdown's shape, which the heading and packing rules were
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

    // Collapses runs of whitespace other than non-breaking spaces, which markitdown keeps. The non-breaking space
    // is written as an escape: a literal U+00A0 looks like a space and is easily lost when the code is copied.
    [GeneratedRegex(@"[^\S ]+")]
    private static partial Regex WhitespaceRunRegex();

    // A list marker alone in its block: a bullet glyph, or a footnote mark like "(1)" or "(a)". MSFT and ORCL lay out a
    // list item as a flex <div> holding the marker's <span> and the text's own <div>, so the marker reads as a paragraph
    // of its own, a blank line from its text. Not "6." - a note's number before its title, which the heading rules read.
    [GeneratedRegex(@"^(?:[•●◦▪·o]|\([a-z0-9]{1,2}\))$")]
    private static partial Regex ListMarkerRegex();

    // A positioned line's inline style: "margin-top" and "line-height" must not read as "top" and "height".
    [GeneratedRegex(@"(?<![-\w])top:\s*(-?[\d.]+)pt")]
    private static partial Regex TopRegex();

    [GeneratedRegex(@"(?<![-\w])line-height:\s*([\d.]+)pt")]
    private static partial Regex LineHeightRegex();

    [GeneratedRegex(@"(?<![-\w])left:\s*(-?[\d.]+)pt")]
    private static partial Regex LeftRegex();

    [GeneratedRegex(@"font-weight:\s*(?:bold|[6-9]00)")]
    private static partial Regex BoldRegex();

    // A page's printed number: "39", "F-26", "iv".
    [GeneratedRegex(@"^(?:(?:F-)?\d{1,4}|[ivx]{1,5})$")]
    private static partial Regex PageNumberRegex();

    /// <param name="notes">Where the notes sit (<see cref="NoteTopics"/>): each block read inside one gets its topic.</param>
    /// <param name="xbrl">The filing's XBRL: a roll-forward row gets its period from its contexts, named by the filer's
    /// fiscal calendar (<see cref="PeriodLabels"/>).</param>
    public static List<FilingBlock> Read(IDocument document, IReadOnlyList<NoteSpan>? notes = null, XbrlDocument? xbrl = null)
    {
        Reader reader = new(notes ?? [], xbrl?.Contexts, xbrl is null ? null : FiscalCalendar.From(xbrl));
        if (document.Body is not null)
        {
            reader.ReadChildren(document.Body);
        }

        return JoinGapsToNextNote(reader.Finish());
    }

    /// <summary>
    /// A block between two notes belongs to the next one. NDAQ and NFLX tag a note from its title, leaving its number
    /// outside ("2." + "SUMMARY OF SIGNIFICANT ACCOUNTING"), so the heading paragraph begins outside the note and
    /// would split off as a section of its own; the other gaps are page furniture (a "Table of Contents" link, a lone
    /// non-breaking space) at a page break before the next note. Every gap between two notes in the four filings is
    /// one of the two.
    /// </summary>
    private static List<FilingBlock> JoinGapsToNextNote(List<FilingBlock> blocks)
    {
        int first = blocks.FindIndex(b => b.Topic is not null);
        int last = blocks.FindLastIndex(b => b.Topic is not null);

        // Likewise the Notes' own title ("NOTES TO CONSOLIDATED FINANCIAL STATEMENTS" - where the section rules
        // already start a section) and what follows it up to the first note - Note 1's number, a date: on its own it
        // would be a title-only chunk of a few tokens, the kind that takes top-5 slots for statement questions.
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
    /// financial path can't linearize becomes text rows, not a pipe table of mostly empty cells: e.g. an exhibit
    /// index page whose management-contract exhibits ("10.6*") read as financial - a text label beside a number
    /// ("10.4", the referenced exhibit) - then fail the financial path ("two values in row '10.6*' map to one
    /// column").
    /// </summary>
    internal static TableBlock? ReadTable(IHtmlTableElement table, IReadOnlyDictionary<string, XbrlContext>? contexts = null, FiscalCalendar? calendar = null)
    {
        LinearizedTable result = HtmlTableLinearizer.Linearize(table);
        if (result.Kind == LinearizedTableKind.Fallback)
        {
            result = HtmlTableLinearizer.LinearizeAsText(table);
        }
        else if (result.Kind == LinearizedTableKind.Financial && contexts is not null)
        {
            result = PeriodLabels.Apply(result, contexts, calendar);
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
    /// piece of a split block - otherwise a later piece reads "4.24 | Description of Securities | 10-K | 6/30/2024 |
    /// 4.26 | 7/30/2024" with no "Exhibit Number | ... | Form | ... | Exhibit" above it.
    /// </summary>
    internal static RowBlock TextRowBlock(LinearizedTable table, int headerRows)
    {
        IReadOnlyList<string> lines = table.TextLines;
        return headerRows > 0 && headerRows < lines.Count
            ? new RowBlock(string.Join(" / ", lines.Take(headerRows)), lines.Skip(headerRows).ToList())
            : new RowBlock(null, lines);
    }

    // An ordered list item's number: the list's start (1 unless set) plus the <li> elements before it - not the node
    // index, which counts the whitespace text between items too.
    private static int ListNumber(IElement item)
    {
        IElement list = item.ParentElement!;
        int start = int.TryParse(list.GetAttribute("start"), out int s) ? s : 1;
        return start + list.Children.TakeWhile(c => c != item).Count(c => c.LocalName == "li");
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

    private sealed class Reader(IReadOnlyList<NoteSpan> notes, IReadOnlyDictionary<string, XbrlContext>? contexts, FiscalCalendar? calendar)
    {
        private readonly List<FilingBlock> blocks = [];
        private readonly StringBuilder paragraph = new();
        private readonly Dictionary<IElement, string> noteStarts = notes.ToDictionary(n => n.Start, n => n.Topic);
        private readonly HashSet<IElement> noteEnds = notes.Select(n => n.End).ToHashSet();

        // The note being read (from its start element through the end of its end element), and the one the
        // current paragraph began in - a paragraph belongs where its first text is.
        private string? topic;
        private string? paragraphTopic;
        private bool paragraphHasText;

        // A list marker held for the paragraph after it (ListMarkerRegex), with the topic it was read in.
        private (string Marker, string? Topic)? pendingMarker;

        // The positioned line the open paragraph ends with, while the next line may still continue it.
        private PositionedLine? lastLine;

        // The paragraphs read from positioned lines - only these join across a column or page break.
        private readonly HashSet<FilingBlock> printedParagraphs = new(ReferenceEqualityComparer.Instance);

        public List<FilingBlock> Finish()
        {
            FlushParagraph();
            FlushMarker();
            JoinColumnBreaks();
            return blocks;
        }

        // A paragraph that runs off the bottom of a column or page goes on at the top of the next, in another
        // container, so it was read as two. Joined when the first part ends mid-sentence and the second starts
        // lowercase; a page number between them (page furniture, which the heading rules drop) stays after the join.
        private void JoinColumnBreaks()
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                if (blocks[i] is not TextBlock first || !printedParagraphs.Contains(first) || EndsSentence(first.Paragraph))
                {
                    continue;
                }

                int next = i + 1;
                while (next < blocks.Count && blocks[next] is TextBlock { Paragraph: var furniture } && PageNumberRegex().IsMatch(furniture))
                {
                    next++;
                }

                if (next < blocks.Count && blocks[next] is TextBlock second && printedParagraphs.Contains(second)
                    && second.Topic == first.Topic && char.IsLower(second.Paragraph[0]))
                {
                    string separator = first.Paragraph[^1] is '-' or '—' or '–' ? string.Empty : " ";
                    TextBlock joined = first with { Paragraph = first.Paragraph + separator + second.Paragraph };
                    printedParagraphs.Add(joined);
                    blocks[i] = joined;
                    blocks.RemoveAt(next);
                    i--;
                }
            }
        }

        private static bool EndsSentence(string paragraph) =>
            paragraph.TrimEnd('”', '"', '’', ')') is { Length: > 0 } text && text[^1] is '.' or '?' or '!' or ':' or ';';

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

            // A table or a link is read whole, never walked into, so a note's start or end inside one would be missed:
            // the last such element inside decides - a start gives the table its note, an end closes the note after it.
            IElement? lastInside = IsReadWhole(element)
                ? element.Descendants<IElement>().LastOrDefault(e => noteStarts.ContainsKey(e) || noteEnds.Contains(e))
                : null;
            if (lastInside is not null && noteStarts.TryGetValue(lastInside, out string? startsInside))
            {
                topic = startsInside;
            }

            ReadElement(element);

            if (noteEnds.Contains(element) || (lastInside is not null && noteEnds.Contains(lastInside) && !noteStarts.ContainsKey(lastInside)))
            {
                topic = null;
            }
        }

        private static bool IsReadWhole(IElement element) =>
            element is IHtmlTableElement || (element.LocalName == "a" && element.GetAttribute("href") is { Length: > 0 });

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
                    FlushMarker();
                    blocks.Add(new TextBlock("---") { Topic = topic });
                    return;
                case "table" when element is IHtmlTableElement table:
                    FlushParagraph();
                    FlushMarker();
                    if (ReadTable(table, contexts, calendar) is { } block)
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
                    paragraph.Append(element.ParentElement?.LocalName == "ol" ? $"{ListNumber(element)}. " : "* ");
                    ReadChildren(element);
                    FlushParagraph();
                    return;
            }

            if (element.LocalName == "div" && PositionedLine.From(element) is { } line)
            {
                // Left open: the next line may continue the paragraph. A note starting on this line ends it.
                if (lastLine is { } previous && line.Continues(previous) && topic == paragraphTopic)
                {
                    JoinLine();
                }
                else
                {
                    FlushParagraph();
                }

                ReadChildren(element);
                lastLine = line;
                return;
            }

            // An empty positioned anchor (a link target) can sit between two lines of one paragraph.
            if (lastLine is not null && BlockTags.Contains(element.LocalName) && element.TextContent.Trim().Length == 0
                && !element.Descendants<IElement>().Any(e => e.LocalName is "img" or "hr" or "br" or "table"))
            {
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

        // The next printed line of the same paragraph: a space between, none after a hyphen or dash ending the line -
        // in these filings always a compound's own ("third-party", "post-investment"), never a word broken to fit.
        private void JoinLine()
        {
            int end = paragraph.Length;
            while (end > 0 && paragraph[end - 1] == ' ')
            {
                end--;
            }

            paragraph.Length = end;
            if (end > 0 && paragraph[end - 1] is not ('-' or '—' or '–'))
            {
                paragraph.Append(' ');
            }
        }

        private void FlushParagraph()
        {
            bool printed = lastLine is not null;
            lastLine = null;
            string lines = string.Join('\n', paragraph.ToString().Split('\n').Select(l => l.Trim(' ')).Where(l => l.Length > 0));
            paragraph.Clear();
            paragraphHasText = false;
            if (lines.Length == 0)
            {
                return;
            }

            if (pendingMarker is null && ListMarkerRegex().IsMatch(lines))
            {
                pendingMarker = (lines, paragraphTopic);
                return;
            }

            if (pendingMarker is { } held)
            {
                (lines, paragraphTopic) = ($"{held.Marker} {lines}", held.Topic);
                pendingMarker = null;
            }

            TextBlock block = new(lines) { Topic = paragraphTopic };
            blocks.Add(block);
            if (printed)
            {
                printedParagraphs.Add(block);
            }
        }

        // A marker with no paragraph after it (a table or the end follows) stays a block of its own, where it was.
        private void FlushMarker()
        {
            if (pendingMarker is { } held)
            {
                blocks.Add(new TextBlock(held.Marker) { Topic = held.Topic });
                pendingMarker = null;
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

    /// <summary>
    /// One printed line of a fixed page layout (NDAQ's filing agent): a &lt;div&gt; placed by an inline "top:..pt" with
    /// a "line-height", its text spans by "left:..pt". Every line is its own &lt;div&gt;, so read as blocks a paragraph
    /// fell apart into its lines - sentences and wrapped headings ("Item 7. Management's Discussion and Analysis of")
    /// cut at every line end. Lines of one paragraph are spaced 1.2 line heights apart, paragraphs at least 1.4: a
    /// line continues the one before it in the same container (a column; a page's next column starts back at its
    /// top) when it starts at the same left edge or under a hanging indent (a bullet's text, 9 or 18pt in) - never
    /// further left, which is centered text: a statement title ("Consolidated Statements of Income") stays a line of
    /// its own above its centered, equally bold "(in millions...)" line, since the title rules read whole lines. Both
    /// sides of the break must be bold or both not, so a bold heading never runs into its first plain line, while a
    /// wrapped heading joins. A rule line ("____") never joins: it sets footnotes apart.
    /// </summary>
    private sealed record PositionedLine(IElement Container, double Top, double LineHeight, double Left, bool StartsBold, bool EndsBold, bool IsRule)
    {
        // Deeper than a hanging indent (9 or 18pt), far narrower than the gap between columns; a rounding margin left.
        private const double MaxHangingIndent = 20;
        private const double EdgeRounding = 0.5;

        // Between the line pitch (1.2) and the narrowest paragraph spacing (1.4).
        private const double MaxLinePitch = 1.25;

        public static PositionedLine? From(IElement div)
        {
            string style = div.GetAttribute("style") ?? string.Empty;
            Match top = TopRegex().Match(style);
            Match lineHeight = LineHeightRegex().Match(style);
            string text = div.TextContent.Trim();
            if (!top.Success || !lineHeight.Success || text.Length == 0 || div.ParentElement is null
                || div.Descendants<IElement>().Any(e => e.LocalName is "table" or "div"))
            {
                return null;
            }

            double? left = div.Descendants<IElement>()
                .Where(e => e.TextContent.Trim().Length > 0)
                .Select(e => LeftRegex().Match(e.GetAttribute("style") ?? string.Empty))
                .FirstOrDefault(m => m.Success) is { } m ? Points(m) : null;
            List<IElement> runs = div.Descendants<IElement>()
                .Where(e => e.GetAttribute("style")?.Contains("font-weight") == true && e.TextContent.Trim().Length > 0)
                .ToList();
            if (left is null || runs.Count == 0)
            {
                return null;
            }

            return new PositionedLine(div.ParentElement, Points(top), Points(lineHeight), left.Value,
                IsBold(runs[0]), IsBold(runs[^1]), text.All(c => c == '_'));
        }

        public bool Continues(PositionedLine previous) =>
            Container == previous.Container
            && !IsRule && !previous.IsRule
            && StartsBold == previous.EndsBold
            && Left - previous.Left is > -EdgeRounding and < MaxHangingIndent
            && Top > previous.Top && Top - previous.Top <= MaxLinePitch * previous.LineHeight;

        private static double Points(Match m) => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

        private static bool IsBold(IElement run) => BoldRegex().IsMatch(run.GetAttribute("style")!);
    }

    private static string Collapse(string text) => WhitespaceRunRegex().Replace(text, " ");

    // markitdown escapes the Markdown emphasis characters in text ("\*" for a footnote asterisk).
    private static string Escape(string text) => text.Replace("*", "\\*").Replace("_", "\\_");
}
