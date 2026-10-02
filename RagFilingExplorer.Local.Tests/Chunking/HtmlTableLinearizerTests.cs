using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;

namespace RagFilingExplorer.Local.Tests.Chunking;

/// <summary>
/// Each fixture reproduces a layout found in a real filing during the linearization spike (see
/// Decision-Log.md, "linearized tables as a second chunking strategy") - reduced to the cells that matter,
/// with column positions kept as they were, since positions are all the linearizer aligns by.
/// </summary>
[TestFixture]
public class HtmlTableLinearizerTests
{
    private static IHtmlTableElement Table(string html) =>
        (IHtmlTableElement)new HtmlParser().ParseDocument($"<html><body>{html}</body></html>").QuerySelector("table")!;

    private static string Tr(params string[] cells) => "<tr>" + string.Concat(cells.Select(c => c.StartsWith("<td") ? c : $"<td>{c}</td>")) + "</tr>";

    private static string Rendered(LinearizedTable table) => string.Join('\n', HtmlTableLinearizer.Render(table));

    private static LinearizedRow Row(LinearizedTable table, string label) => table.Rows.Single(r => r.Label == label);

    // MSFT: no colspan at all; the year sits over the "$" cell, one column left of its number.
    [Test]
    public void NoColspan_YearOverDollarCell_ValuesGoUnderTheirOwnYear()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("(In millions)", "", "", "", "", "", "")
            + Tr("Year Ended June 30,", "", "2026", "", "", "2025", "")
            + Tr("Net income", "", "$", "133,749", "", "$", "101,832")
            + Tr("Comprehensive income", "", "$", "133,812", "", "$", "104,075")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Financial));
        Assert.That(table.Units, Is.EqualTo("(In millions)"));
        Assert.That(Row(table, "Comprehensive income").Values.Select(v => (v.ColumnLabel, v.Text)), Is.EqualTo(new[]
        {
            ("Year Ended June 30, 2026", "$133,812"),
            ("Year Ended June 30, 2025", "$104,075"),
        }));
    }

    // NFLX/NDAQ: colspan everywhere, and the units as the *last* header row, spanning every column. Taken
    // as a column header it used to swallow the whole header block into one column.
    [Test]
    public void UnitsRowSpanningEveryColumnBelowTheYears_IsTheUnitsNotAColumn()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("<td colspan=\"3\"></td>", "<td colspan=\"6\">Year ended December 31,</td>")
            + Tr("<td colspan=\"3\"></td>", "<td colspan=\"3\">2025</td>", "<td colspan=\"3\">2024</td>")
            + Tr("<td colspan=\"3\"></td>", "<td colspan=\"6\">(in thousands)</td>")
            + Tr("<td colspan=\"3\">Comprehensive income</td>", "$", "10,038,657", "", "$", "9,297,738", "")
            + "</table>"));

        Assert.That(table.Units, Is.EqualTo("(in thousands)"));
        Assert.That(Row(table, "Comprehensive income").Values.Select(v => (v.ColumnLabel, v.Text)), Is.EqualTo(new[]
        {
            ("Year ended December 31, 2025", "$10,038,657"),
            ("Year ended December 31, 2024", "$9,297,738"),
        }));
    }

    // MSFT's share repurchases: the year row sits *below* Shares/Amount and spans each pair - a group
    // header, not a caption for the whole table.
    [Test]
    public void YearRowBelowItsSubColumns_StacksIntoEachSubColumnLabel()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("(In millions)", "", "<td colspan=\"2\">Shares</td>", "<td colspan=\"2\">Amount</td>", "<td colspan=\"2\">Shares</td>", "<td colspan=\"2\">Amount</td>")
            + Tr("Year Ended June 30,", "", "<td colspan=\"4\">2026</td>", "<td colspan=\"4\">2025</td>")
            + Tr("First Quarter", "", "", "8", "$", "3,955", "", "7", "$", "2,800")
            + "</table>"));

        Assert.That(Row(table, "First Quarter").Values.Select(v => (v.ColumnLabel, v.Text)), Is.EqualTo(new[]
        {
            ("Year Ended June 30, 2026 Shares", "8"),
            ("Year Ended June 30, 2026 Amount", "$3,955"),
            ("Year Ended June 30, 2025 Shares", "7"),
            ("Year Ended June 30, 2025 Amount", "$2,800"),
        }));
    }

    // NFLX's MD&A "Change 2025 vs. 2024" (and both tax-rate reconciliations): one header over an amount
    // and a percentage is the table's own structure - both stay under it, not a conflict.
    [Test]
    public void OneHeaderOverAmountAndPercentage_KeepsBothAsSubColumns()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "", "<td colspan=\"2\">2025</td>", "<td colspan=\"3\">Change 2025 vs. 2024</td>")
            + Tr("Revenues", "", "$", "45,183,036", "$", "6,182,070", "16%")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Financial));
        Assert.That(Rendered(table), Does.Contain("Change 2025 vs. 2024: $6,182,070 / 16%"));
    }

    // NDAQ: group labels whose cell spans wider than the usual label column. Judged by where they end,
    // they read as column headers; by where they start, they're labels.
    [Test]
    public void GroupLabelWiderThanTheLabelColumn_IsAGroupLabelNotAHeader()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "2025", "2024")
            + Tr("Market Services", "$4,214", "$3,771")
            + Tr("<td colspan=\"2\">Transaction-based expenses:</td>", "")
            + Tr("Transaction rebates", "(2,572)", "(2,026)")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Financial));
        LinearizedRow rebates = Row(table, "Transaction rebates");
        Assert.That(rebates.GroupLabel, Is.EqualTo("Transaction-based expenses:"));
        Assert.That(rebates.Values.Select(v => v.ColumnLabel), Is.EqualTo(new[] { "2025", "2024" }));
    }

    // MSFT's investment tables: a date in the label column below the headers, then section labels. The
    // date must reach every row, not be overwritten by the next section label.
    [Test]
    public void PeriodRowInTheLabelColumn_StaysOnEveryRowPathBelowIt()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "Adjusted Cost Basis", "Recorded Basis")
            + Tr("June 30, 2026", "", "")
            + Tr("Changes in Fair Value Recorded in Other Comprehensive Income", "", "")
            + Tr("Commercial paper", "$2,987", "$2,987")
            + "</table>"));

        Assert.That(Row(table, "Commercial paper").GroupLabel,
            Is.EqualTo("June 30, 2026 > Changes in Fair Value Recorded in Other Comprehensive Income"));
    }

    // MSFT's exhibit index ends with rows that carry no numbers; they were read as a header block no data
    // row ever followed, and silently dropped.
    [Test]
    public void TrailingRowsWithoutNumbers_AreKeptAsLines()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "Amount")
            + Tr("10.17*", "1,250")
            + Tr("31.1", "Certification of Chief Executive Officer")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Financial));
        Assert.That(Rendered(table), Does.Contain("31.1 | Certification of Chief Executive Officer"));
    }

    // The safety net: if any cell's text would be missing from the output, the whole table falls back - to
    // Markdown in the Linearized strategy, to text rows in Structured (content intact) - instead of losing it silently.
    [Test]
    public void TableThatWouldLoseCellText_FallsBack()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "2026", "", "Filed Herewith")
            + Tr("Revenue", "100", "", "")
            + Tr("Cost", "40", "", "")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Fallback));
        Assert.That(table.FallbackReason, Does.Contain("Filed Herewith"));
    }

    // Two values that are only *nearest* to the same header (neither under it) are ambiguous: fallback,
    // never a guess.
    [Test]
    public void TwoValuesNearestTheSameHeader_FallsBack()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("", "", "2026", "", "")
            + Tr("Revenue", "", "", "100", "200")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Fallback));
    }

    [Test]
    public void TableWithoutNumbers_IsATextTable()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("Item 1.", "Business")
            + Tr("Item 1A.", "Risk Factors")
            + "</table>"));

        Assert.That(table.Kind, Is.EqualTo(LinearizedTableKind.Text));
        Assert.That(table.TextLines, Is.EqualTo(new[] { "Item 1. | Business", "Item 1A. | Risk Factors" }));
    }

    // The shipped form: units + a caption shared by every column go into the context line once, and nil
    // "—" values are left out.
    [Test]
    public void ToRowBlock_FactorsSharedCaptionIntoContext_AndOmitsNilValues()
    {
        LinearizedTable table = HtmlTableLinearizer.Linearize(Table("<table>"
            + Tr("(In millions)", "", "", "", "")
            + Tr("Year Ended June 30,", "", "2026", "", "2025")
            + Tr("Net income", "", "133,749", "", "101,832")
            + Tr("Other", "", "—", "", "5")
            + "</table>"));

        RowBlock block = HtmlTableLinearizer.ToRowBlock(table);

        Assert.That(block.Context, Is.EqualTo("(In millions) Year Ended June 30,"));
        Assert.That(block.Rows, Is.EqualTo(new[] { "Net income — 2026: 133,749 | 2025: 101,832", "Other — 2025: 5" }));
    }
}
