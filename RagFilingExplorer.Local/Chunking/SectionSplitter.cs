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
    // filing (NFLX-10K-2025.html) has no space in most of its Item headings ("Item 1.Business"), and a
    // \.\s+ requirement would silently lose every one of those as a section boundary, collapsing the
    // filing into a few giant PART-only sections. The relaxed pattern still excludes the bare "Item 1"
    // noise case (no period at all - see BareItemNoiseRegex).
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
    // filing content. The optional "F-" covers financial-statement page numbers ("F-3", NDAQ's), the only
    // other page-marker style across the four filings - otherwise they land in chunks, even as a chunk's
    // overlap text. Lone roman numerals (NDAQ's
    // "i"-"iv") are deliberately not matched: a lone "x" can be a real checkbox mark on a cover page.
    [GeneratedRegex(@"^(?:F-)?\d{1,4}$")]
    private static partial Regex PageNumberRegex();

    // A decorative rule line - e.g. a run of Markdown-escaped underscores ("\_\_\_...") used by some
    // filing agents as a visual divider instead of an <hr>. Zero content value either way.
    [GeneratedRegex(@"^(?:\\?[_\-*=~.]){3,}$")]
    private static partial Regex DecorativeRuleRegex();

    // Back matter after the last Item - the financial statement pages, signatures, exhibit index. Form 10-K
    // lets a filer place the financial statements after Part IV, referenced from Item 8/15: NFLX and NDAQ
    // do, right after "Item 16. Form 10-K Summary - None.", so without this every statement, auditor's report
    // and Note would be headed "Item 16. Form 10-K Summary" - in the embedding text and in the model's
    // citations. Each title here is a standalone line that appears only as back matter across
    // all four filings (the auditor's report title isn't usable: it also appears inside Items 8 and 9A).
    [GeneratedRegex(@"^(?:(?<fs>INDEX\s+TO\s+(?:CONSOLIDATED\s+)?FINANCIAL\s+STATEMENTS)|(?<sig>SIGNATURES)|(?<ex>EXHIBIT\s+INDEX|INDEX\s+OF\s+EXHIBITS))$", RegexOptions.IgnoreCase)]
    private static partial Regex BackMatterRegex();

    public static List<DocumentSection> Split(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
        List<DocumentSection> sections = new();
        HeadingTracker headings = new();
        StringBuilder currentBody = new();
        bool inFence = false;

        void FlushSection()
        {
            string body = currentBody.ToString().Trim();
            currentBody.Clear();
            if (body.Length > 0)
            {
                sections.Add(new DocumentSection(headings.Heading, body));
            }
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

            if (inFence || headings.Read(lines[i], FlushSection))
            {
                currentBody.Append(lines[i]).Append('\n');
            }
        }

        FlushSection();
        return sections;
    }

    /// <summary>
    /// The heading rules, one line at a time: which lines are Part/Item/back-matter headings (they change the
    /// heading and are dropped), which are page noise (dropped), which start a new section under the same heading
    /// (statement titles, kept), and which are body. Shared by <see cref="Split"/> (text) and the Structured
    /// strategy (blocks - see Structured.StructuredSections), so both apply the same rules.
    /// </summary>
    internal sealed class HeadingTracker
    {
        private readonly HashSet<string> seenParts = new();
        private string currentPart = string.Empty;
        private string currentItem = string.Empty;

        /// <summary>"PART I &gt; Item 1. Business", or "(no heading)" before the first Part or Item.</summary>
        public string Heading
        {
            get
            {
                string heading = string.Join(" > ", new[] { currentPart, currentItem }.Where(s => s.Length > 0));
                return heading.Length > 0 ? heading : "(no heading)";
            }
        }

        /// <summary>
        /// Reads one line; true if it's body text to keep. <paramref name="startSection"/> is called before the
        /// heading changes (and before a statement title), while <see cref="Heading"/> still names the section
        /// that is ending.
        /// </summary>
        public bool Read(string rawLine, Action startSection)
        {
            string line = rawLine.Trim();

            // Lone page numbers and thematic-break markers ("---") are pagination artifacts with no
            // content value - if left in, they end up as their own noise-only chunk when a page break
            // happens to land between two real paragraphs.
            if (PageNumberRegex().IsMatch(line) || line == "---" || DecorativeRuleRegex().IsMatch(line))
            {
                return false;
            }

            Match partMatch = PartHeaderRegex().Match(line);
            if (partMatch.Success)
            {
                string numeral = partMatch.Groups[1].Value.ToUpperInvariant();
                if (seenParts.Add(numeral))
                {
                    startSection();
                    currentPart = $"PART {numeral}";
                    currentItem = string.Empty;
                }

                return false;
            }

            // A statement title (or the Notes boundary) starts a new section under the same heading, so
            // it always opens a fresh chunk. FilingChunkRecords tags each chunk with the last statement title
            // seen *within* it, so without this, text before a mid-chunk title - the previous
            // statement's page footer, or auditor-report prose (NDAQ) - inherited the next statement's
            // tag. Safe to split on: across all four filings each of these lines matches only the real
            // title, never a table-of-contents entry, so this adds no tiny stray sections.
            if (StatementTypeDetector.IsStatementTypeBoundary(line))
            {
                startSection();
                return true;
            }

            Match itemMatch = TitledItemHeaderRegex().Match(line);
            if (itemMatch.Success)
            {
                startSection();
                currentItem = $"Item {itemMatch.Groups[1].Value}. {itemMatch.Groups[2].Value}";
                return false;
            }

            // Only once Part IV has started: that's where the form puts back matter, and an "Index to
            // Financial Statements" inside a filer's Item 8 is correctly headed by Item 8 already.
            Match backMatterMatch = currentPart == "PART IV" ? BackMatterRegex().Match(line) : Match.Empty;
            if (backMatterMatch.Success)
            {
                startSection();
                currentItem = backMatterMatch.Groups["fs"].Success ? "Financial Statements"
                    : backMatterMatch.Groups["sig"].Success ? "Signatures"
                    : "Exhibit Index";
                return false;
            }

            return !BareItemNoiseRegex().IsMatch(line);
        }
    }
}
