using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;

namespace RagFilingExplorer.Local.Tests.Structured;

/// <summary>
/// The Structured strategy's page reader. Its paragraph text keeps the shape markitdown produced (step 1a: each
/// case below is a markitdown behaviour read off its output for the four filings), since the shared heading and
/// packing rules were written against it; its tables are blocks of their own, never text to parse back.
/// </summary>
[TestFixture]
public class FilingBlockReaderTests
{
    private static List<FilingBlock> Read(string body) => FilingBlockReader.Read(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>"));

    private static List<string> Texts(string body) => Read(body).Select(b => b.Text).ToList();

    // SEC filing agents put each visual line in its own <div>/<p>; the heading rules need "PART I" and
    // "Item 1. Business" to arrive as whole lines of their own to find them.
    [Test]
    public void Read_EachBlockElement_BecomesItsOwnParagraph()
    {
        List<FilingBlock> blocks = Read("<div><p><span>PART I</span></p></div><div><span>Item 1.</span><span> Business</span></div><p>Body text.</p>");

        Assert.That(blocks, Is.All.InstanceOf<TextBlock>());
        Assert.That(blocks.Select(b => b.Text), Is.EqualTo(new[] { "PART I", "Item 1. Business", "Body text." }));
    }

    // Non-breaking spaces are written as escapes here and in the reader: the step-1a converter's regex held a
    // literal U+00A0, which looks like a space and was lost when the code moved into FilingBlockReader - runs of
    // ordinary spaces then survived (ORCL's cover page: "Securities Act.    Yes  ☒").
    [Test]
    public void Read_CollapsesWhitespace_ButKeepsNonBreakingSpaces()
    {
        Assert.That(Texts("<p>Net   income\n  rose sharply</p>"), Is.EqualTo(new[] { "Net income rose sharply" }));
    }

    [Test]
    public void Read_LineBreak_StaysALineBreakWithinTheParagraph()
    {
        Assert.That(Texts("<p>PART II<br/>Item 5. Market</p>"), Is.EqualTo(new[] { "PART II\nItem 5. Market" }));
    }

    // ORCL/NFLX: every page carries a "Table of Contents" link whose text sits in a <span> inside the <a>.
    // Taking only text outside links dropped all 131 of ORCL's, and with them the only content of
    // "Item 6. [Reserved]" - a section that then disappeared from the outline.
    [Test]
    public void Read_LinkWithTextInsideASpan_KeepsItsText()
    {
        Assert.That(Texts("<p><a href=\"#toc_page\"><span style=\"color:#0000ff\">Table of Contents</span></a></p>"),
            Is.EqualTo(new[] { "[Table of Contents](#toc_page)" }));
    }

    [Test]
    public void Read_AsteriskInText_IsEscapedAsMarkitdownDid()
    {
        Assert.That(Texts("<p>Filed herewith*</p>"), Is.EqualTo(new[] { "Filed herewith\\*" }));
    }

    [Test]
    public void Read_RemovesTheHiddenXbrlHeader()
    {
        Assert.That(Texts("<div style=\"display:none\"><ix:header><ix:hidden>dei:DocumentType 10-K</ix:hidden></ix:header></div><p>Visible</p>"),
            Is.EqualTo(new[] { "Visible" }));
    }

    [Test]
    public void Read_TableInsideAParagraph_EndsItAndBecomesItsOwnBlock()
    {
        List<FilingBlock> blocks = Read("<div>Before<table><tr><td>Revenue</td><td>100</td></tr></table>After</div>");

        Assert.That(blocks.Select(b => b.GetType()), Is.EqualTo(new[] { typeof(TextBlock), typeof(TableBlock), typeof(TextBlock) }));
        Assert.That(blocks.Select(b => b.Text), Is.EqualTo(new[] { "Before", "Revenue — 100", "After" }));
    }

    // The point of the block model: the table's linearized values keep their inline-XBRL tags, instead of being
    // flattened to row text before anything could read them.
    [Test]
    public void Read_FinancialTable_KeepsItsTaggedValuesAndElement()
    {
        List<FilingBlock> blocks = Read("<table><tr><td></td><td>2026</td></tr><tr><td>Revenue</td>"
            + "<td><ix:nonFraction name=\"us-gaap:Revenues\" contextRef=\"FY2026\" unitRef=\"usd\">100</ix:nonFraction></td></tr></table>");

        TableBlock table = (TableBlock)blocks.Single();
        LinearizedValue value = table.Table.Rows.Single().Values.Single();
        Assert.That(table.Text, Is.EqualTo("Revenue — 2026: 100"));
        Assert.That((value.Concept, value.ContextRef), Is.EqualTo(("us-gaap:Revenues", "FY2026")));
        Assert.That(table.Element, Is.InstanceOf<IHtmlTableElement>());
    }

    [Test]
    public void Read_DecorativeEmptyTable_IsDropped()
    {
        Assert.That(Read("<p>Text</p><table><tr><td> </td><td></td></tr></table>"), Has.Count.EqualTo(1));
    }

    // MSFT: two of the exhibit index's seven page tables read as financial ("10.6*" beside the number "10.4") and
    // fell back to pipe tables of mostly empty cells, while the other five came out as text rows. Across the four
    // filings the fallback left 504 pipe-table rows; with the text path as fallback, none.
    [Test]
    public void Read_TableTheLinearizerFallsBackOn_BecomesTextRows()
    {
        // The same ambiguous table the Linearized strategy keeps as HTML (LinearizedStrategyTests).
        TableBlock table = (TableBlock)Read("<table><tr><td></td><td></td><td>2026</td><td></td><td></td></tr>"
            + "<tr><td>Revenue</td><td></td><td></td><td>100</td><td>200</td></tr></table>").Single();

        Assert.That(table.Table.Kind, Is.EqualTo(LinearizedTableKind.Text));
        Assert.That(table.Rows.Rows, Is.EqualTo(new[] { "2026", "Revenue | 100 | 200" }));
    }

    // MSFT chunk 198: a continuation piece of an exhibit-index page read "4.24 | Description of Securities | 10-K |
    // 6/30/2024 | 4.26 | 7/30/2024" with no column names above it. The filers mark column names only by bold text
    // (no <thead>, no <th>), so leading all-bold rows become the row block's context, repeated on every piece.
    [Test]
    public void Read_TextTableWithBoldHeaderRows_PutsThemInTheContextLine()
    {
        string bold = "style=\"font-weight:bold\"";
        TableBlock table = (TableBlock)Read($"<table><tr><td><span {bold}>Exhibit Number</span></td><td><span {bold}>Form</span></td></tr>"
            + "<tr><td>4.1</td><td>8-K</td></tr></table>").Single();

        Assert.That(table.Rows.Context, Is.EqualTo("Exhibit Number | Form"));
        Assert.That(table.Rows.Rows, Is.EqualTo(new[] { "4.1 | 8-K" }));
    }

    [Test]
    public void LeadingBoldRowCount_BoldThroughout_IsNoHeader()
    {
        // A bold cover-page box: every row bold, so none of them is a column-name row.
        var table = (IHtmlTableElement)new HtmlParser().ParseDocument(
            "<table><tr><td><b>Washington</b></td></tr><tr><td><b>D.C. 20549</b></td></tr></table>").QuerySelector("table")!;

        Assert.That(HtmlTableLinearizer.LeadingBoldRowCount(table), Is.EqualTo(0));
    }
}
