using System.Globalization;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Structured;

/// <summary>
/// Step 1b-iii-c: a roll-forward row states its own period. In a roll-forward the columns aren't periods (equity
/// components, share counts and prices) and the year follows only from a "Balances as of May 31, 2024" row somewhere
/// above - T9: ORCL's "Cash dividends declared ($1.70 per share) — Accumulated Deficit: (4,743)" is fiscal 2025, which
/// no line of its chunk says, and the model answered with the wrong year's row. Each value's XBRL context carries the
/// period, so the row's label gets it: "... ($1.70 per share) (year ended May 31, 2025)".
///
/// Only where it's missing and needed (measured 2026-09-29): a row whose tagged values all share one period, whose
/// label and column names don't show that period's year, in a table whose rows span more than one period. That is the
/// roll-forwards - equity statements, award activity, goodwill and other comprehensive income - 123 rows in 16 tables
/// across the four filings. Without the last condition 145 more rows in single-period tables would repeat what their
/// table already says.
///
/// Step 1c-a: a filer whose fiscal year isn't the calendar year also gets the year's name, from its own calendar
/// (<see cref="FiscalCalendar"/>) - "(fiscal 2025, year ended May 31, 2025)". T9 asks for "fiscal 2025"; 1b-iii-c's
/// label put the right row, "year ended May 31, 2025", in front of the model, which didn't connect the two.
/// </summary>
internal static class PeriodLabels
{
    public static LinearizedTable Apply(LinearizedTable table, IReadOnlyDictionary<string, XbrlContext> contexts, FiscalCalendar? calendar = null)
    {
        List<XbrlContext?> periods = table.Rows.Select(r => SinglePeriod(r, contexts)).ToList();
        if (periods.OfType<XbrlContext>().Select(Key).Distinct().Count() < 2)
        {
            return table;
        }

        List<LinearizedRow> rows = table.Rows.Select((row, i) => periods[i] is { } period && row.Label.Length > 0 && !ShowsYear(row, period)
            ? row with { Label = $"{row.Label} ({Describe(period, calendar)})" }
            : row).ToList();
        return table with { Rows = rows };
    }

    /// <summary>
    /// "year ended May 31, 2025" - "fiscal 2025, year ended May 31, 2025" with a non-calendar fiscal year - "three
    /// months ended ...", "as of June 30, 2025", "on November 1, 2023".
    /// </summary>
    internal static string Describe(XbrlContext period, FiscalCalendar? calendar = null)
    {
        string Date(DateOnly d) => d.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        if (period.Instant is { } instant)
        {
            return $"as of {Date(instant)}";
        }

        DateOnly start = period.StartDate!.Value;
        DateOnly end = period.EndDate!.Value;
        int days = end.DayNumber - start.DayNumber;
        return days switch
        {
            0 => $"on {Date(end)}",
            >= 350 and <= 380 when calendar is { IsCalendarYear: false } && calendar.FiscalYearEnding(end) is { } fiscal
                => $"fiscal {fiscal}, year ended {Date(end)}",
            >= 350 and <= 380 => $"year ended {Date(end)}",
            >= 85 and <= 95 => $"three months ended {Date(end)}",
            _ => $"{Date(start)} to {Date(end)}",
        };
    }

    // The one period all of a row's tagged values share, or null (none tagged, or several - a row across years).
    private static XbrlContext? SinglePeriod(LinearizedRow row, IReadOnlyDictionary<string, XbrlContext> contexts)
    {
        List<XbrlContext> tagged = row.Values
            .Select(v => v.ContextRef is not null && contexts.TryGetValue(v.ContextRef, out XbrlContext? c) ? c : null)
            .OfType<XbrlContext>()
            .ToList();
        return tagged.Count > 0 && tagged.Select(Key).Distinct().Count() == 1 ? tagged[0] : null;
    }

    private static (DateOnly?, DateOnly?, DateOnly?) Key(XbrlContext c) => (c.Instant, c.StartDate, c.EndDate);

    private static bool ShowsYear(LinearizedRow row, XbrlContext period)
    {
        string year = (period.Instant ?? period.EndDate)!.Value.Year.ToString(CultureInfo.InvariantCulture);
        return $"{row.GroupLabel} {row.Label} {string.Join(" ", row.Values.Select(v => v.ColumnLabel))}".Contains(year, StringComparison.Ordinal);
    }
}
