using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Structured;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Structured;

/// <summary>
/// Step 1b-iii-c: a roll-forward row gets its period from its values' XBRL contexts - only where the row doesn't
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
}
