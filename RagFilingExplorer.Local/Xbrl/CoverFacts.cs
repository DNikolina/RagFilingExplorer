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

    /// <summary>The name a question uses: the registrant name without its legal form ("Microsoft", "Nasdaq").</summary>
    public static string ShortName(string registrantName)
    {
        string name = registrantName.Trim();
        for (Match m = LegalFormRegex().Match(name); m.Success && m.Index > 0; m = LegalFormRegex().Match(name))
        {
            name = name[..m.Index].Trim();
        }

        return name;
    }

    /// <summary>
    /// The common stock's trading symbol and exchange. Each registered security has its own context pairing its
    /// title, symbol and exchange; only the common stock's counts - NDAQ also lists four note issues ("NDAQ29"),
    /// ORCL its preferred stock ("ORCL PRD") first.
    /// </summary>
    public static (string Symbol, string? Exchange)? CommonStock(XbrlDocument xbrl)
    {
        XbrlFact? title = xbrl.Facts.FirstOrDefault(f => f.Concept == "dei:Security12bTitle"
            && f.Text?.Contains("common stock", StringComparison.OrdinalIgnoreCase) == true);
        if (title is null || Clean(InContext("dei:TradingSymbol")) is not { } symbol)
        {
            return null;
        }

        return (symbol, Clean(InContext("dei:SecurityExchangeName")));

        string? InContext(string concept) => xbrl.Facts.FirstOrDefault(f => f.Concept == concept && f.ContextRef == title.ContextRef)?.Text;
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
            v = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v.ToLowerInvariant());
            foreach (string suffix in new[] { "LLP", "LLC", "LP", "USA" })
            {
                v = Regex.Replace(v, $@"\b{suffix}\b", suffix, RegexOptions.IgnoreCase);
            }
        }

        return v;
    }
}
