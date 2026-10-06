using System.Globalization;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// How the filer names its fiscal years, from its own cover tags: the year the report covers
/// (<c>dei:DocumentFiscalYearFocus</c>, e.g. 2026) ends on <c>dei:DocumentPeriodEndDate</c> (2026-05-31), and the
/// years before it end a year apart - so a year ending May 31, 2025 is ORCL's fiscal 2025. Named from the focus, not
/// the end date: a filer whose year ends in January may call the year ending January 31, 2026 "fiscal 2025".
/// Questions say "fiscal 2025" where the filing's rows say "year ended May 31, 2025".
/// </summary>
internal sealed record FiscalCalendar(int FocusYear, DateOnly PeriodEnd)
{
    // A 52/53-week year ends on a weekday near the same date, not on it.
    private const int ToleranceDays = 7;

    /// <summary>The calendar a filing tags, or null when it tags neither or a malformed value.</summary>
    public static FiscalCalendar? From(XbrlDocument xbrl) =>
        int.TryParse(xbrl.First("dei:DocumentFiscalYearFocus")?.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int focus)
        && DateOnly.TryParseExact(xbrl.First("dei:DocumentPeriodEndDate")?.Text?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly end)
            ? new FiscalCalendar(focus, end)
            : null;

    /// <summary>
    /// True when the fiscal year ends on December 31: its years are calendar years, and "2025" in a row already
    /// names them - no "fiscal" name is added (NDAQ, NFLX).
    /// </summary>
    public bool IsCalendarYear => PeriodEnd.Month == 12 && PeriodEnd.Day == 31;

    /// <summary>The fiscal year a year-long period ending on <paramref name="end"/> is, or null if it doesn't end at a fiscal year-end.</summary>
    public int? FiscalYearEnding(DateOnly end)
    {
        for (int k = -10; k <= 10; k++)
        {
            if (Math.Abs(end.DayNumber - PeriodEnd.AddYears(k).DayNumber) <= ToleranceDays)
            {
                return FocusYear + k;
            }
        }

        return null;
    }
}
