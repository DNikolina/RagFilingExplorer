using System.Globalization;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// The inline XBRL format codes these filings use, per the Transformation Registry versions they declare
/// (ixt 2020-02-12 - NDAQ, NFLX; ixt 2022-02-16 - MSFT, ORCL; SEC's ixt-sec 2015-08-31 - all four). Each code
/// turns the displayed text into the stored value. The code names used here mean the same in both ixt
/// versions. An unknown code throws: a guessed conversion would produce a quietly wrong number.
///
/// The SEC's cover-page codes that map a name to an EDGAR code (exchange, state, filer category) keep the
/// displayed text as their value: the full code lists aren't needed for anything read from them.
/// </summary>
internal static partial class IxTransformations
{
    private static readonly string[] NumberWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve",
         "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty"];

    [GeneratedRegex(@"^\d{1,3}(?:[,  ]?\d{3})*(?:\.\d+)?$|^\d+(?:\.\d+)?$")]
    private static partial Regex NumDotDecimalRegex();

    /// <summary>The displayed text of a number fact to its unscaled, unsigned value.</summary>
    public static decimal ToNumber(string? format, string text)
    {
        string t = text.Trim();
        switch (Local(format))
        {
            case null:
                // No format: the text must already be an xs:decimal (Inline XBRL 1.1, 4.1.5.1).
                return decimal.Parse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            case "num-dot-decimal":
                if (!NumDotDecimalRegex().IsMatch(t))
                {
                    throw new FormatException($"'{text}' isn't valid for ixt:num-dot-decimal");
                }

                return decimal.Parse(t.Replace(",", "").Replace(" ", "").Replace(" ", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            case "fixed-zero":
                return 0m;
            case "numwordsen":
                return WordsToNumber(t);
            default:
                throw new NotSupportedException($"Unknown number format '{format}' (text '{text}') - add it from the Transformation Registry rather than guess.");
        }
    }

    /// <summary>The displayed text of a text fact to its value: a date, duration or flag in canonical form, or the text.</summary>
    public static string ToText(string? format, string text)
    {
        string t = text.Trim();
        return Local(format) switch
        {
            null => t,
            "fixed-true" => "true",
            "fixed-false" => "false",
            "boolballotbox" => t switch { "☒" => "true", "☐" => "false", _ => throw new FormatException($"'{text}' isn't a ballot box") },
            "date-monthname-day-year-en" => ParseDate(t, "MMMM d, yyyy").ToString("yyyy-MM-dd"),
            "date-year-month-day" => ParseDate(t, "yyyy-MM-dd").ToString("yyyy-MM-dd"),
            "date-monthname-year-en" => ParseDate(t, "MMMM yyyy").ToString("yyyy-MM"),
            "date-monthname-day-en" => "--" + ParseDate(t + " 2000", "MMMM d yyyy").ToString("MM-dd"),
            "duryear" => YearsToDuration(WordsToNumber(t)),
            "durmonth" => $"P{WordsToNumber(t)}M",
            "durday" => $"P{WordsToNumber(t)}D",
            "durwordsen" => DurationFromWords(t),
            "exchnameen" or "stateprovnameen" or "entityfilercategoryen" => t,
            _ => throw new NotSupportedException($"Unknown text format '{format}' (text '{text}') - add it from the Transformation Registry rather than guess."),
        };
    }

    // "ixt:num-dot-decimal" -> "num-dot-decimal"; the prefix names the registry version, not the rule.
    private static string? Local(string? format) => format is null ? null : format[(format.IndexOf(':') + 1)..].ToLowerInvariant();

    private static DateOnly ParseDate(string text, string pattern) =>
        DateOnly.ParseExact(Regex.Replace(text.Replace(" ", " "), @"\s+", " ").Trim(), pattern, CultureInfo.InvariantCulture);

    // "three" -> 3, "15" -> 15; "no"/"none" -> 0 (ixt-sec numwordsen reads "No" as zero).
    private static decimal WordsToNumber(string text)
    {
        string t = text.Trim().ToLowerInvariant();
        if (decimal.TryParse(t.Replace(",", ""), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal n))
        {
            return n;
        }

        if (t is "no" or "none")
        {
            return 0m;
        }

        int index = Array.IndexOf(NumberWords, t);
        return index >= 0 ? index : throw new FormatException($"'{text}' isn't a supported number word");
    }

    // A fractional number of years becomes years, months and days, as EDGAR's own extraction does: the fraction of a
    // year times 12 gives months; the fraction of a month times an average month (365.25 / 12 = 30.4375 days),
    // truncated, gives days. Fitted to all seven fractional durations in the four filings: MSFT 2.3 = P2Y3M18D,
    // NFLX 1.53 = P1Y6M10D, ORCL 7.58 = P7Y6M29D, NDAQ 2.1 / 3.2 / 8.4 = 6, 12, 24 days - a 30-day month got ORCL
    // wrong (28D) and rounding got NFLX wrong (11D). Found by comparing every fact against EDGAR's extracted instance.
    private static string YearsToDuration(decimal years)
    {
        int whole = (int)Math.Floor(years);
        decimal months = (years - whole) * 12m;
        int wholeMonths = (int)Math.Floor(months);
        int days = (int)Math.Floor((months - wholeMonths) * 365.25m / 12m);
        string duration = (whole > 0 ? $"{whole}Y" : "") + (wholeMonths > 0 ? $"{wholeMonths}M" : "") + (days > 0 ? $"{days}D" : "");
        return "P" + (duration.Length > 0 ? duration : "0Y");
    }

    // "six years" -> P6Y, "15 years" -> P15Y, "one year and six months" -> P1Y6M. A hyphen counts as a space:
    // "one-year", and NDAQ's "one- year" (hyphenated across a line break).
    private static string DurationFromWords(string text)
    {
        string years = "", months = "", days = "";
        foreach (Match m in Regex.Matches(text.ToLowerInvariant().Replace('-', ' '), @"([a-z]+|\d+)\s+(year|month|day)s?"))
        {
            string value = WordsToNumber(m.Groups[1].Value).ToString(CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value)
            {
                case "year": years = value + "Y"; break;
                case "month": months = value + "M"; break;
                default: days = value + "D"; break;
            }
        }

        string duration = years + months + days;
        return duration.Length > 0 ? "P" + duration : throw new FormatException($"'{text}' isn't a duration in words");
    }
}
