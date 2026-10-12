using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;

namespace RagFilingExplorer.Tests.Structured;

/// <summary>
/// The Structured strategy's page reader. Its paragraph text keeps the shape markitdown produced (each
/// case below is a markitdown behaviour read off its output for the four filings), since the shared heading and
/// packing rules were written against it; its tables are blocks of their own, never text to parse back.
/// </summary>
[TestFixture]
public class FilingBlockReaderTests
{
    private static List<FilingBlock> Read(string body) => FilingBlockReader.Read(new HtmlParser().ParseDocument($"<html><body>{body}</body></html>"));

    private static List<string> Texts(string body) => Read(body).Select(b => b.Text).ToList();

    // A table is read whole, so a note starting or ending inside one was missed: the note lost its topic, or its topic
    // ran on past its end.
    [Test]
    public void Read_NoteStartingOrEndingInsideATable_TopicFollowsIt()
    {
        IDocument document = new HtmlParser().ParseDocument("<html><body>"
            + "<p>before</p>"
            + "<table><tr><td><span id=\"s\">5. INCOME TAXES</span></td><td>1,000</td></tr></table>"
            + "<p>Income taxes text.</p>"
            + "<table><tr><td>Deferred taxes</td><td><span id=\"e\">250</span></td></tr></table>"
            + "<p>after</p></body></html>");
        NoteSpan note = new(document.GetElementById("s")!, document.GetElementById("e")!, "Income Taxes");

        List<FilingBlock> blocks = FilingBlockReader.Read(document, [note]);

        Assert.That(blocks.Select(b => (b.Text, b.Topic)), Is.EqualTo(new (string, string?)[]
        {
            ("before", null),
            (blocks[1].Text, "Income Taxes"),
            ("Income taxes text.", "Income Taxes"),
            (blocks[3].Text, "Income Taxes"),
            ("after", null),
        }));
    }

    // Numbering came from the node index, which counts the whitespace between items: "2. A", "4. B".
    [Test]
    public void Read_OrderedList_NumberedByItem()
    {
        Assert.That(Texts("<ol>\n<li>A</li>\n<li>B</li>\n</ol>"), Is.EqualTo(new[] { "1. A", "2. B" }));
        Assert.That(Texts("<ol start=\"3\"><li>C</li><li>D</li></ol>"), Is.EqualTo(new[] { "3. C", "4. D" }));
    }

    // SEC filing agents put each visual line in its own <div>/<p>; the heading rules need "PART I" and
    // "Item 1. Business" to arrive as whole lines of their own to find them.
    [Test]
    public void Read_EachBlockElement_BecomesItsOwnParagraph()
    {
        List<FilingBlock> blocks = Read("<div><p><span>PART I</span></p></div><div><span>Item 1.</span><span> Business</span></div><p>Body text.</p>");

        Assert.That(blocks, Is.All.InstanceOf<TextBlock>());
        Assert.That(blocks.Select(b => b.Text), Is.EqualTo(new[] { "PART I", "Item 1. Business", "Body text." }));
    }

    // MSFT and ORCL: a list item is a flex <div> holding the marker's <span> and the text's own <div>, so each bullet
    // and footnote mark became a paragraph of its own, a blank line above its text - ORCL's note numbers too ("6." over
    // "NOTES PAYABLE AND OTHER BORROWINGS"). A marker with only a table after it stays where it was.
    [Test]
    public void Read_ListMarkerInItsOwnBlock_JoinsTheParagraphAfterIt()
    {
        static string Item(string marker, string text) => $"<div style=\"display:flex\"><span>{marker}</span><div><span>{text}</span></div></div>";

        List<string> texts = Texts(Item("&#8226;", "We tested controls.") + Item("o", "Obtained contracts.") + Item("(1)", "Comprises EMEA.")
            + "<p>6.</p><p>NOTES PAYABLE</p><p>(a)</p><table><tr><td>Debt</td><td>1</td></tr></table>");

        Assert.That(texts.Take(4), Is.EqualTo(new[] { "• We tested controls.", "o Obtained contracts.", "(1) Comprises EMEA.", "6. NOTES PAYABLE" }));
        Assert.That(texts[4], Is.EqualTo("(a)"));
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

    // ORCL/NFLX: a link's text can sit in a <span> inside the <a> - taking only text outside links dropped it.
    [Test]
    public void Read_LinkWithTextInsideASpan_KeepsItsText()
    {
        Assert.That(Texts("<p><a href=\"#note_6\"><span style=\"color:#0000ff\">Note 6</span></a></p>"),
            Is.EqualTo(new[] { "[Note 6](#note_6)" }));
    }

    // ORCL and NFLX: every page's "Table of Contents" back-link (NFLX's the whole EDGAR address, ~40 tokens) was in the
    // chunks - six chunks held nothing else. Page furniture, like a page number.
    [Test]
    public void Read_TableOfContentsLink_IsDropped()
    {
        Assert.That(Texts("<p><a href=\"#toc_page\"><span>Table of Contents</span></a></p><p>Body text.</p>"),
            Is.EqualTo(new[] { "Body text." }));
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

    // One printed line of NDAQ's fixed page layout: a positioned <div> placed by top, its text span by left.
    private static string Line(double top, double left, string text, bool bold = false) =>
        $"<div style=\"line-height:10pt;position:var(--position);top:{top}pt;width:612pt\">"
        + $"<span style=\"font-weight:{(bold ? "bold" : "normal")};left:{left}pt;position:var(--position)\">{text}</span></div>";

    // NDAQ: every printed line is its own positioned <div>, so each line read as a paragraph - sentences cut at every
    // line end, chunks starting mid-sentence, and Item 7's heading lost "Financial Condition and Results of
    // Operations" to the body text. Lines 12pt apart are one paragraph, 18pt apart a new one; a hanging indent and
    // an empty anchor between lines stay inside it, and a line-end hyphen joins without a space.
    [Test]
    public void Read_PositionedLinesOfOneParagraph_JoinIntoIt()
    {
        List<string> texts = Texts("<div>"
            + Line(45, 49.5, "Item 7. Management’s Discussion and Analysis of", bold: true)
            + Line(57, 49.5, "Financial Condition and Results of Operations", bold: true)
            + Line(75, 49.5, "Institutional investors conduct pre- ")
            + "<div style=\"position:absolute;top:87pt\" id=\"anchor\"></div>"
            + Line(87, 49.5, "investment due diligence. ")
            + Line(105, 49.5, "•Bullet text that")
            + Line(117, 58.5, "wraps under its indent.")
            + "</div>");

        Assert.That(texts, Is.EqualTo(new[]
        {
            "Item 7. Management’s Discussion and Analysis of Financial Condition and Results of Operations",
            "Institutional investors conduct pre-investment due diligence.",
            "•Bullet text that wraps under its indent.",
        }));
    }

    // NDAQ: a statement title and its "(in millions...)" line are both bold and centered one line apart; joined, the
    // title is no longer a whole line for the title rules. A bold heading over plain text and a rule line over a
    // footnote stay apart too.
    [Test]
    public void Read_PositionedTitleOverCenteredOrPlainLine_StaysALineOfItsOwn()
    {
        List<string> texts = Texts("<div>"
            + Line(57, 230.6, "Consolidated Statements of Income", bold: true)
            + Line(69, 223.3, "(in millions, except per share amounts)", bold: true)
            + Line(87, 49.5, "Tax Matters", bold: true)
            + Line(99, 49.5, "The following table presents our provision.")
            + Line(111, 49.5, "____________")
            + Line(123, 49.5, "(1) Primarily relates to taxes.")
            + "</div>");

        Assert.That(texts, Is.EqualTo(new[]
        {
            "Consolidated Statements of Income", "(in millions, except per share amounts)", "Tax Matters",
            "The following table presents our provision.", "\\_\\_\\_\\_\\_\\_\\_\\_\\_\\_\\_\\_", "(1) Primarily relates to taxes.",
        }));
    }

    // NDAQ: a paragraph running off the bottom of a column went on at the top of the next one, a new container, and
    // was read as two - the second starting mid-sentence. The next page's number between them stays, after the join.
    [Test]
    public void Read_PositionedParagraphAcrossAColumnBreak_JoinsWhenItContinuesMidSentence()
    {
        List<string> texts = Texts(
            "<div>" + Line(724, 49.5, "Our platforms help clients manage") + "</div>"
            + "<div>" + Line(22, 304, "4") + "</div>"
            + "<div>" + Line(45, 315, "their workflows.") + Line(63, 315, "Next paragraph ends") + "</div>"
            + "<div>" + Line(45, 49.5, "New paragraph.") + "</div>");

        Assert.That(texts, Is.EqualTo(new[]
        {
            "Our platforms help clients manage their workflows.", "4", "Next paragraph ends", "New paragraph.",
        }));
    }

    // NDAQ positions a bullet's text past the bullet's end, NFLX pads it away - neither has a space in the text, so
    // every bullet read "•our products and services;". A run starting where the last one ended (a font change inside a
    // word) still joins without one.
    [Test]
    public void Read_RunSetApartByLayout_GetsASpace()
    {
        List<string> texts = Texts(
            "<div><div style=\"line-height:10pt;position:var(--position);top:349.2pt;width:612pt\">"
            + "<span style=\"font-weight:normal;left:49.5pt;position:var(--position);width:3.5pt\">•</span>"
            + "<span style=\"font-weight:normal;left:58.5pt;position:var(--position);width:40pt\">our products</span>"
            + "<span style=\"font-weight:normal;left:98.5pt;position:var(--position);width:6pt\">’s</span></div></div>"
            + "<div style=\"padding-left:36pt;text-indent:-18pt\"><span>•</span><span style=\"padding-left:14.5pt\">our ability</span></div>");

        Assert.That(texts, Is.EqualTo(new[] { "• our products’s", "• our ability" }));
    }
}
