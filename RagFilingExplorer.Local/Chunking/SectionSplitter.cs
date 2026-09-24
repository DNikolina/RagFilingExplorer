using System.Text;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Chunking;

/// <summary>
/// MarkItDown's HTML-&gt;Markdown conversion of SEC EDGAR 10-K filings produces zero real Markdown
/// headings: "PART I" / "Item 1. Business" render as plain paragraph text, because the source HTML
/// uses bold-styled &lt;p&gt; tags instead of &lt;h1&gt;-&lt;h6&gt;. Filing agents also vary: some
/// (e.g. Microsoft's) repeat "PART I" / "Item 1" as a running per-page header, others don't.
///
/// This splits the converted text directly into (heading, body) sections by pattern-matching the
/// Item/Part boundary lines, dropping the repeated running-header noise along the way.
/// </summary>
internal static partial class SectionSplitter
{
    // "Item 1. Business" / "ITEM 1C. Cybersecurity" - a real heading, always carries a title after the
    // period. The space between the period and the title is optional (\.\s*, not \.\s+): Netflix's
    // filing (NFLX-10K-2025.html) has no space at all in 21 of its 22 Item headings ("Item 1.Business"),
    // confirmed directly by inspecting the converted output - a \.\s+ requirement would have silently
    // lost every one of those as a section boundary, collapsing the whole filing into 4 giant PART-only
    // sections with no Item-level heading. Verified the relaxed pattern still correctly excludes the
    // bare "Item 1" noise case (no period at all - see BareItemNoiseRegex) and still matches every
    // other filer's with-space convention.
    [GeneratedRegex(@"^Item\s+(\d{1,2}[A-Za-z]?)\.\s*(\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitledItemHeaderRegex();

    // Bare "Item 1" (no period, no title) - running page-header noise, never a real boundary.
    [GeneratedRegex(@"^Item\s+\d{1,2}[A-Za-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex BareItemNoiseRegex();

    // "PART I" / "PART II." - real boundary only on first occurrence; repeats are page-header noise.
    [GeneratedRegex(@"^PART\s+([IVX]{1,4})\.?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PartHeaderRegex();

    // A lone page number - matches unconditionally wherever it appears, not just when adjacent to a
    // "---" thematic break (there's no adjacency check in Split; this regex alone decides). Assumes a
    // standalone 1-4 digit line is always pagination noise (a page number or footer) and never real
    // filing content. The optional "F-" covers financial-statement page numbers ("F-3"): NDAQ has 45
    // of them, the only other page-marker style found across all four filings, and they were landing
    // in chunks - one even carried forward as a chunk's overlap text. Lone roman numerals (NDAQ's
    // "i"-"iv") are deliberately not matched: a lone "x" can be a real checkbox mark on a cover page.
    [GeneratedRegex(@"^(?:F-)?\d{1,4}$")]
    private static partial Regex PageNumberRegex();

    // A decorative rule line - e.g. a run of Markdown-escaped underscores ("\_\_\_...") used by some
    // filing agents as a visual divider instead of an <hr>. Zero content value either way.
    [GeneratedRegex(@"^(?:\\?[_\-*=~.]){3,}$")]
    private static partial Regex DecorativeRuleRegex();

    public static List<DocumentSection> Split(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        HashSet<string> seenParts = new();
        List<DocumentSection> sections = new();

        string currentPart = string.Empty;
        string currentItem = string.Empty;
        StringBuilder currentBody = new();
        bool inFence = false;

        void FlushSection()
        {
            string body = currentBody.ToString().Trim();
            currentBody.Clear();
            if (body.Length == 0)
            {
                return;
            }

            string heading = string.Join(" > ", new[] { currentPart, currentItem }.Where(s => s.Length > 0));
            sections.Add(new DocumentSection(heading.Length > 0 ? heading : "(no heading)", body));
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();

            // A fenced block is a linearized table (see RowBlock), copied through untouched. Its rows can
            // look like headings: a table of contents linearizes to "PART I" / "Item 1. — Page: ..." lines,
            // and "PART I" only counts as a boundary the first time it's seen, so a TOC read as headings
            // would hijack every Part heading after it. The Markdown strategy's output has no fences (no
            // filing has a <pre>), so this changes nothing there.
            if (line.StartsWith(RowBlock.Fence, StringComparison.Ordinal))
            {
                inFence = !inFence;
                currentBody.Append(lines[i]).Append('\n');
                continue;
            }

            if (inFence)
            {
                currentBody.Append(lines[i]).Append('\n');
                continue;
            }

            // Lone page numbers and thematic-break markers ("---") are pagination artifacts with no
            // content value - if left in, they end up as their own noise-only chunk when a page break
            // happens to land between two real paragraphs.
            if (PageNumberRegex().IsMatch(line) || line == "---" || DecorativeRuleRegex().IsMatch(line))
            {
                continue;
            }

            Match partMatch = PartHeaderRegex().Match(line);
            if (partMatch.Success)
            {
                string numeral = partMatch.Groups[1].Value.ToUpperInvariant();
                if (seenParts.Add(numeral))
                {
                    FlushSection();
                    currentPart = $"PART {numeral}";
                    currentItem = string.Empty;
                }

                continue;
            }

            // A statement title (or the Notes boundary) starts a new section under the same heading, so
            // it always opens a fresh chunk. BuildRecords tags each chunk with the last statement title
            // seen *within* it, so without this, text before a mid-chunk title - the previous
            // statement's page footer, or auditor-report prose (NDAQ) - inherited the next statement's
            // tag. Safe to split on: across all four filings each of these lines matches only the real
            // title, never a table-of-contents entry, so this adds no tiny stray sections.
            if (StatementTypeDetector.IsStatementTypeBoundary(line))
            {
                FlushSection();
                currentBody.Append(lines[i]).Append('\n');
                continue;
            }

            Match itemMatch = TitledItemHeaderRegex().Match(line);
            if (itemMatch.Success)
            {
                FlushSection();
                currentItem = $"Item {itemMatch.Groups[1].Value}. {itemMatch.Groups[2].Value}";
                continue;
            }

            if (BareItemNoiseRegex().IsMatch(line))
            {
                continue;
            }

            currentBody.Append(lines[i]).Append('\n');
        }

        FlushSection();
        return sections;
    }
}
