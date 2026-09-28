using AngleSharp.Html.Parser;
using Microsoft.ML.Tokenizers;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// The Structured strategy's step 1a: HtmlTextConverter must produce the text shape markitdown did, since
/// SectionSplitter and TokenChunker read that shape unchanged. Each case below is a markitdown behaviour read
/// off its output for the four filings; the strategy's output matched the Linearized strategy's chunks on
/// all four (section outlines and every distinct line identical, 989 of 999 chunks word for word).
/// </summary>
[TestFixture]
public class StructuredStrategyTests
{
    private static string Convert(string body) => HtmlTextConverter.Convert(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>"));

    // SEC filing agents put each visual line in its own <div>/<p>; SectionSplitter needs "PART I" and
    // "Item 1. Business" to arrive as whole lines of their own to find them.
    [Test]
    public void Convert_EachBlockElement_BecomesItsOwnParagraph()
    {
        string text = Convert("<div><p><span>PART I</span></p></div><div><span>Item 1.</span><span> Business</span></div><p>Body text.</p>");

        Assert.That(text, Is.EqualTo("PART I\n\nItem 1. Business\n\nBody text.\n"));
    }

    [Test]
    public void Convert_CollapsesWhitespace_ButKeepsNonBreakingSpaces()
    {
        string text = Convert("<p>Net   income\n  rose sharply</p>");

        Assert.That(text, Is.EqualTo("Net income rose sharply\n"));
    }

    // ORCL/NFLX: every page carries a "Table of Contents" link whose text sits in a <span> inside the <a>.
    // Taking only text outside links dropped all 131 of ORCL's, and with them the only content of
    // "Item 6. [Reserved]" - a section that then disappeared from the outline.
    [Test]
    public void Convert_LinkWithTextInsideASpan_KeepsItsText()
    {
        string text = Convert("<p><a href=\"#toc_page\"><span style=\"color:#0000ff\">Table of Contents</span></a></p>");

        Assert.That(text, Is.EqualTo("[Table of Contents](#toc_page)\n"));
    }

    [Test]
    public void Convert_AsteriskInText_IsEscapedAsMarkitdownDid()
    {
        Assert.That(Convert("<p>Filed herewith*</p>"), Is.EqualTo("Filed herewith\\*\n"));
    }

    [Test]
    public void Convert_PreBlock_BecomesAFencedRowBlock()
    {
        string text = Convert("<p>Intro</p><pre>#rows\nRevenue — 2026: 100</pre>");

        Assert.That(RowBlock.TryParse(text.Split("\n\n")[1]), Is.Not.Null);
        Assert.That(text, Does.Contain("```\n#rows\nRevenue — 2026: 100\n```"));
    }

    // MSFT/NFLX exhibit indexes (tables the linearizer leaves alone): with no <th> row, markitdown makes up an
    // empty header row, and a merged cell is followed by one empty cell per extra column. Without the made-up
    // header the first real row became the header, and TokenChunker's table splitting shifted by a row.
    [Test]
    public void Convert_FallbackTableWithoutHeaderRow_GetsAnEmptyHeaderAndPaddedMergedCells()
    {
        string text = Convert("<table><tr><td>Exhibit<div>Number</div></td><td colspan=\"2\">Incorporated by Reference</td></tr>"
            + "<tr><td>3.1</td><td>Form</td><td>Date</td></tr></table>");

        Assert.That(text, Is.EqualTo(
            "|  |  |  |\n| --- | --- | --- |\n| Exhibit Number | Incorporated by Reference | |\n| 3.1 | Form | Date |\n"));
    }

    // MSFT: two of the exhibit index's seven page tables read as financial ("10.6*" beside the number "10.4") and
    // fell back to pipe tables of mostly empty cells, while the other five came out as text rows. Across the four
    // filings the fallback left 504 pipe-table rows; with the text path as fallback, none.
    [Test]
    public void ConvertToText_TableTheLinearizerFallsBackOn_BecomesTextRowsNotAPipeTable()
    {
        // The same ambiguous table the Linearized strategy keeps as HTML (LinearizedStrategyTests).
        string html = "<html><body><table><tr><td></td><td></td><td>2026</td><td></td><td></td></tr>"
            + "<tr><td>Revenue</td><td></td><td></td><td>100</td><td>200</td></tr></table></body></html>";

        string text = StructuredChunkingStrategy.ConvertToText(html);

        Assert.That(text, Does.Not.Contain("| --- |"));
        Assert.That(text, Does.Contain("```\n#rows\n2026\nRevenue | 100 | 200\n```"));
    }

    // MSFT chunk 198: a continuation piece of an exhibit-index page read "4.24 | Description of Securities | 10-K |
    // 6/30/2024 | 4.26 | 7/30/2024" with no column names above it. The filers mark column names only by bold text
    // (no <thead>, no <th>), so leading all-bold rows become the row block's context, repeated on every piece.
    [Test]
    public void ConvertToText_TextTableWithBoldHeaderRows_RepeatsThemOnEveryPiece()
    {
        string bold = "style=\"font-weight:bold\"";
        string rows = string.Concat(Enumerable.Range(1, 30).Select(i => $"<tr><td>4.{i}</td><td>Supplemental Indenture number {i} between the Company and the Trustee</td><td>8-K</td></tr>"));
        string html = $"<html><body><table><tr><td><span {bold}>Exhibit Number</span></td><td><span {bold}>Exhibit Description</span></td><td><span {bold}>Form</span></td></tr>{rows}</table></body></html>";

        string text = StructuredChunkingStrategy.ConvertToText(html);
        List<(string Content, int Tokens)> chunks = TokenChunker.Chunk(text, TiktokenTokenizer.CreateForModel("gpt-4"), maxTokens: 150, overlapTokens: 0);

        Assert.That(chunks, Has.Count.GreaterThan(2));
        Assert.That(chunks.All(c => c.Content.StartsWith("Exhibit Number | Exhibit Description | Form\n")), Is.True);
    }

    [Test]
    public void LeadingBoldRowCount_BoldThroughout_IsNoHeader()
    {
        // A bold cover-page box: every row bold, so none of them is a column-name row.
        var table = (AngleSharp.Html.Dom.IHtmlTableElement)new HtmlParser().ParseDocument(
            "<table><tr><td><b>Washington</b></td></tr><tr><td><b>D.C. 20549</b></td></tr></table>").QuerySelector("table")!;

        Assert.That(HtmlTableLinearizer.LeadingBoldRowCount(table), Is.EqualTo(0));
    }

    [Test]
    public void ConvertToText_RemovesTheHiddenXbrlHeader()
    {
        string html = "<html><body><div style=\"display:none\"><ix:header><ix:hidden>dei:DocumentType 10-K</ix:hidden></ix:header></div><p>Visible</p></body></html>";

        Assert.That(StructuredChunkingStrategy.ConvertToText(html), Is.EqualTo("Visible\n"));
    }

    [Test]
    public void ChunkingStrategies_CreatesTheStructuredStrategy()
    {
        ChunkingSettings settings = new() { Strategy = ChunkingStrategyKind.Structured, TokenizerModel = "gpt-4", MaxTokensPerChunk = 500, OverlapTokens = 50 };

        Assert.That(ChunkingStrategies.Create(settings), Is.InstanceOf<StructuredChunkingStrategy>());
        Assert.That(ChunkingStrategies.FileName(ChunkingStrategyKind.Structured), Is.EqualTo("structured"));
    }
}
