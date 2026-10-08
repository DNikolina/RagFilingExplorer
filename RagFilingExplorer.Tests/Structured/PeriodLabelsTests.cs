using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Tests.Structured;

/// <summary>
/// A roll-forward row gets its period from its values' XBRL contexts - only where the row doesn't
/// show it and the table spans more than one period.
/// </summary>
[TestFixture]
public class PeriodLabelsTests
{
    private static readonly Dictionary<string, XbrlContext> Contexts = new()
    {
        ["fy24"] = new("fy24", null, new DateOnly(2023, 6, 1), new DateOnly(2024, 5, 31), []),
        ["fy25"] = new("fy25", null, new DateOnly(2024, 6, 1), new DateOnly(2025, 5, 31), []),
        ["end24"] = new("end24", new DateOnly(2024, 5, 31), null, null, []),
        ["day"] = new("day", null, new DateOnly(2023, 11, 1), new DateOnly(2023, 11, 1), []),
        ["q"] = new("q", null, new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), []),
    };

    private static LinearizedRow Row(string label, params (string Column, string? Context)[] values) =>
        new(null, label, values.Select(v => new LinearizedValue("k", v.Column, "1", v.Context, "us-gaap:X", "usd")).ToList());

    private static LinearizedTable Table(params LinearizedRow[] rows) => new(LinearizedTableKind.Financial, null, null, rows, []);

    // ORCL T9: "Cash dividends declared ($1.70 per share) — Accumulated Deficit: (4,743)" is fiscal 2025, which only the
    // "Balances as of May 31, 2024" row above it implies; the model answered with another year's row.
    [Test]
    public void Apply_RollForwardRowsWithoutTheirYear_GetTheirPeriod()
    {
        LinearizedTable table = Table(
            Row("Balances as of May 31, 2024", ("Accumulated Deficit", "end24")),
            Row("Cash dividends declared ($1.70 per share)", ("Accumulated Deficit", "fy25"), ("Total Stockholders' Equity", "fy25")));

        List<string> labels = PeriodLabels.Apply(table, Contexts).Rows.Select(r => r.Label).ToList();

        Assert.That(labels, Is.EqualTo(new[] { "Balances as of May 31, 2024", "Cash dividends declared ($1.70 per share) (year ended May 31, 2025)" }));
    }

    [Test]
    public void Apply_ColumnsThatArePeriods_LeaveTheRowAlone()
    {
        LinearizedTable table = Table(
            Row("Net income", ("2025", "fy25"), ("2024", "fy24")),
            Row("Dividends", ("2025", "fy25")));

        Assert.That(PeriodLabels.Apply(table, Contexts).Rows.Select(r => r.Label), Is.EqualTo(new[] { "Net income", "Dividends" }));
    }

    // 145 rows of single-period tables would only repeat what their table says.
    [Test]
    public void Apply_TableOfOnePeriod_IsUnchanged()
    {
        LinearizedTable table = Table(Row("Granted", ("Shares", "fy25")), Row("Vested", ("Shares", "fy25")));

        Assert.That(PeriodLabels.Apply(table, Contexts), Is.SameAs(table));
    }

    [Test]
    public void Apply_UntaggedAndEmptyLabelRows_AreLeftAlone()
    {
        LinearizedTable table = Table(Row("Granted", ("Shares", "fy24")), Row("Other", ("Shares", null)), Row("", ("Shares", "fy25")));

        Assert.That(PeriodLabels.Apply(table, Contexts).Rows.Select(r => r.Label), Is.EqualTo(new[] { "Granted (year ended May 31, 2024)", "Other", "" }));
    }

    [TestCase("fy25", "year ended May 31, 2025")]
    [TestCase("end24", "as of May 31, 2024")]
    [TestCase("day", "on November 1, 2023")]
    [TestCase("q", "three months ended June 30, 2026")]
    public void Describe_Periods(string context, string expected)
    {
        Assert.That(PeriodLabels.Describe(Contexts[context]), Is.EqualTo(expected));
    }

    // T9 asks for "fiscal 2025"; the row said only "year ended May 31, 2025" and the model didn't connect them.
    // ORCL's calendar: fiscal 2026 ends May 31, 2026 (DocumentFiscalYearFocus, DocumentPeriodEndDate).
    [TestCase("fy25", "fiscal 2025, year ended May 31, 2025")]
    [TestCase("fy24", "fiscal 2024, year ended May 31, 2024")]
    [TestCase("end24", "as of May 31, 2024")]
    [TestCase("q", "three months ended June 30, 2026")]
    public void Describe_NonCalendarFiscalYear_NamesTheYearFromTheFilersCalendar(string context, string expected)
    {
        Assert.That(PeriodLabels.Describe(Contexts[context], new FiscalCalendar(2026, new DateOnly(2026, 5, 31))), Is.EqualTo(expected));
    }

    // NDAQ, NFLX: "2025" already names a calendar year.
    [Test]
    public void Describe_CalendarFiscalYear_AddsNoFiscalName()
    {
        XbrlContext year = new("y", null, new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31), []);

        Assert.That(PeriodLabels.Describe(year, new FiscalCalendar(2025, new DateOnly(2025, 12, 31))), Is.EqualTo("year ended December 31, 2025"));
    }

    // Named from the focus year, not the end date's year: a retailer's year ending January 31, 2026 is its fiscal 2025.
    // A 52/53-week year ends within a week of the same date.
    [TestCase(2025, 2026, 1, 31, 2026, 2, 1, 2025)]
    [TestCase(2025, 2026, 1, 31, 2025, 2, 2, 2024)]
    [TestCase(2026, 2026, 5, 31, 2024, 5, 31, 2024)]
    public void FiscalYearEnding_NamedFromTheFocusYear(int focus, int ey, int em, int ed, int y, int m, int d, int expected)
    {
        Assert.That(new FiscalCalendar(focus, new DateOnly(ey, em, ed)).FiscalYearEnding(new DateOnly(y, m, d)), Is.EqualTo(expected));
    }

    [Test]
    public void FiscalYearEnding_DateNotAtAYearEnd_IsNull()
    {
        Assert.That(new FiscalCalendar(2026, new DateOnly(2026, 5, 31)).FiscalYearEnding(new DateOnly(2025, 11, 30)), Is.Null);
    }
}
