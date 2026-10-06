using System.Globalization;
using System.Text.RegularExpressions;

namespace RagFilingExplorer.Local.Xbrl;

/// <summary>
/// The tagged cover-page facts (dei:) both the filing profile (<see cref="FilingProfile"/>) and company
/// registration (Retrieval.CompanyRegistry) read, cleaned the same way for both.
/// </summary>
internal static partial class CoverFacts
{
    // A legal form at the end of a registrant name: "Nasdaq, Inc.", "Oracle Corporation".
    [GeneratedRegex(@",?\s+(Corporation|Corp\.?|Incorporated|Inc\.?|Company|Co\.?|Limited|Ltd\.?|plc|LLC|L\.?P\.?|N\.V\.|S\.A\.|AG|SE)$", RegexOptions.IgnoreCase)]
    private static partial Regex LegalFormRegex();

    /// <summary>The registrant's name as tagged, cleaned ("Microsoft Corporation"), or null.</summary>
    public static string? RegistrantName(XbrlDocument xbrl) => Clean(xbrl.First("dei:EntityRegistrantName")?.Text);

    /// <summary>The name a question uses: the registrant name without a leading "The" or its legal form ("Microsoft",
    /// "Nasdaq", "Coca-Cola" for "The Coca-Cola Company") - questions don't say "The Coca-Cola's revenue".</summary>
    public static string ShortName(string registrantName)
    {
        string name = registrantName.Trim();
        if (name.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
        {
            name = name[4..].Trim();
        }

        for (Match m = LegalFormRegex().Match(name); m.Success && m.Index > 0; m = LegalFormRegex().Match(name))
        {
            name = name[..m.Index].Trim();
        }

        return name;
    }

    // A class of common equity, by its registered title: "Common Stock, $0.01 par value", "Class A Common Stock",
    // "Common Shares", "Ordinary Shares" - not notes ("4.500% Senior Notes due 2032") or preferred stock.
    [GeneratedRegex(@"\b(common|ordinary)\s+(stock|shares?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommonEquityRegex();

    /// <summary>
    /// Every class of common equity's trading symbol, exchange and title, in cover order. Each registered security
    /// has its own context pairing its title, symbol and exchange; only common equity counts - NDAQ also lists four
    /// note issues ("NDAQ29"), ORCL its preferred stock ("ORCL PRD") first. A filer with several classes (Alphabet's
    /// GOOGL and GOOG) has one entry per class; the four filings here have one each.
    /// </summary>
    public static List<(string Symbol, string? Exchange, string Title)> CommonStocks(XbrlDocument xbrl)
    {
        List<(string Symbol, string? Exchange, string Title)> classes = new();
        foreach (XbrlFact title in xbrl.Facts.Where(f => f.Concept == "dei:Security12bTitle" && f.Text is not null && CommonEquityRegex().IsMatch(f.Text)))
        {
            // A symbol is trimmed, never re-cased: Clean's title case for all-capital values turned "GOOGL" into "Googl".
            if (InContext("dei:TradingSymbol", title.ContextRef)?.Trim() is { Length: > 0 } symbol && classes.All(c => c.Symbol != symbol))
            {
                classes.Add((symbol, Clean(InContext("dei:SecurityExchangeName", title.ContextRef)), Clean(title.Text)!));
            }
        }

        return classes;

        string? InContext(string concept, string contextRef) => xbrl.Facts.FirstOrDefault(f => f.Concept == concept && f.ContextRef == contextRef)?.Text;
    }

    /// <summary>
    /// The line that opens every chunk's embedding text - "Oracle Corporation (ORCL), Form 10-K for fiscal year
    /// 2026." - from the filing's own cover facts. With it, a number-heavy table chunk stops losing to prose
    /// (docs/Decision-Log.md, "Step 1d").
    /// Null without a registrant name.
    /// </summary>
    public static string? EmbeddingContext(XbrlDocument xbrl)
    {
        if (RegistrantName(xbrl) is not { } name)
        {
            return null;
        }

        List<string> symbols = CommonStocks(xbrl).Select(c => c.Symbol).ToList();
        string? form = Clean(xbrl.First("dei:DocumentType")?.Text);
        string? year = Clean(xbrl.First("dei:DocumentFiscalYearFocus")?.Text);
        return $"{name}{(symbols.Count == 0 ? "" : $" ({string.Join(", ", symbols)})")}"
            + $"{(form is null ? "" : $", Form {form}")}{(year is null ? "" : $" for fiscal year {year}")}.";
    }

    /// <summary>
    /// Cover values as tagged: "New York," (a trailing comma inside the tag), "MICROSOFT CORPORATION" (all capitals
    /// on MSFT's cover). All-capital values become title case, keeping entity suffixes ("LLP") capitalised.
    /// </summary>
    public static string? Clean(string? value)
    {
        string? v = value?.Trim().TrimEnd(',', ';').Trim();
        if (string.IsNullOrEmpty(v))
        {
            return null;
        }

        if (v.Any(char.IsLetter) && v.Where(char.IsLetter).All(char.IsUpper) && v.Count(char.IsLetter) > 4)
        {
            v = string.Join(" ", v.Split(' ').Select(TitleCaseWord));
            foreach (string suffix in new[] { "LLP", "LLC", "LP", "USA" })
            {
                v = Regex.Replace(v, $@"\b{suffix}\b", suffix, RegexOptions.IgnoreCase);
            }
        }

        return v;
    }

    // One word of an all-capital value in title case - unless it reads as an acronym: no vowel ("KPMG", "NY") or an
    // ampersand ("AT&T"), which title case would turn into "Kpmg" and "At&T".
    private static string TitleCaseWord(string word) =>
        word.Any(char.IsLetter) && (!word.Any(c => "AEIOU".Contains(char.ToUpperInvariant(c))) || word.Contains('&'))
            ? word
            : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word.ToLowerInvariant());
}
