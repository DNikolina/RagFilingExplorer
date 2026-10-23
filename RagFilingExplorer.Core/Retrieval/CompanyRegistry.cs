using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Xbrl;

namespace RagFilingExplorer.Core.Retrieval;

/// <summary>
/// One filing, the names a question can use for its company ("Microsoft", "MSFT"), and its company line ("Microsoft
/// Corporation (MSFT), Form 10-K for fiscal year 2026." - <see cref="CoverFacts.EmbeddingContext"/>), which opens each of
/// its chunks for the reranker as it opens their embedding text. <paramref name="Cik"/> (the SEC's company number) and
/// <paramref name="FiscalYear"/> tell two filings of one company apart.
/// </summary>
internal sealed record CompanyRegistration(string Filing, IReadOnlyList<string> Names, string? Context = null, string? Cik = null, string? FiscalYear = null);

/// <summary>
/// Which filing(s) a question names, for the company filter. Built from each filing's own tagged cover facts - the
/// registrant name without its legal form ("MICROSOFT CORPORATION" -> "Microsoft", "Nasdaq, Inc." -> "Nasdaq") and
/// the trading symbol of each class of common stock (not notes or preferred stock). No hand-kept table to forget: a
/// company without an entry runs its questions unfiltered across every filing, which can produce a hallucinated
/// figure. Every 10-K tags its cover page in inline XBRL, so a filing added to data/ registers itself; one without a
/// registrant name fails at startup instead (docs/Decision-Log.md, "registration from the cover facts").
///
/// Names match as whole words, ignoring case - a substring match would misread a short name ("Meta" in "metadata").
///
/// Only 10-K annual reports register: a 10-Q's quarterly figures or a partial 10-K/A would answer beside the annual
/// ones, and the filing profile would call them an annual report. Two filings of one company for the same fiscal year
/// are refused - nothing could tell which one a question means.
/// </summary>
internal sealed class CompanyRegistry(IReadOnlyList<CompanyRegistration> registrations)
{
    public IReadOnlyList<CompanyRegistration> Registrations { get; } = registrations;

    /// <summary>Reads each filing's cover facts - each page is parsed, so this costs a little time per filing at startup.</summary>
    public static CompanyRegistry FromFilings(IEnumerable<FileInfo> filings)
    {
        List<CompanyRegistration> registrations = filings.Select(filing => Register(filing.Name, ReadXbrl(filing))).ToList();
        EnsureOneFilingPerCompanyYear(registrations);
        return new(registrations);
    }

    public static CompanyRegistration Register(string filing, XbrlDocument xbrl)
    {
        string name = CoverFacts.RegistrantName(xbrl)
            ?? throw new InvalidOperationException($"{filing} tags no dei:EntityRegistrantName, so questions naming its company can't be routed to it.");
        string? form = CoverFacts.Clean(xbrl.First("dei:DocumentType")?.Text);
        if (form != "10-K")
        {
            throw new InvalidOperationException($"{filing} is {(form is null ? "untagged (no dei:DocumentType)" : $"a Form {form}")} - only 10-K annual reports can be indexed.");
        }

        List<string> names = [CoverFacts.ShortName(name), .. CoverFacts.CommonStocks(xbrl).Select(c => c.Symbol)];

        return new CompanyRegistration(filing, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), CoverFacts.EmbeddingContext(xbrl),
            CoverFacts.Clean(xbrl.First("dei:EntityCentralIndexKey")?.Text), CoverFacts.Clean(xbrl.First("dei:DocumentFiscalYearFocus")?.Text));
    }

    /// <summary>Fails naming the filings when two are the same company's (by CIK, else by name) for the same fiscal year.</summary>
    internal static void EnsureOneFilingPerCompanyYear(IReadOnlyList<CompanyRegistration> registrations)
    {
        string[] duplicates = registrations
            .GroupBy(r => (Company: r.Cik ?? r.Names[0], r.FiscalYear))
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(r => r.Filing))} ({g.First().Names[0]}, fiscal year {g.Key.FiscalYear ?? "untagged"})")
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Two filings of one company for the same fiscal year: {string.Join("; ", duplicates)} - keep one of each in data/.");
        }
    }

    // A file that isn't an inline XBRL filing fails inside the reader, which doesn't know the file's name.
    private static XbrlDocument ReadXbrl(FileInfo filing)
    {
        try
        {
            return InlineXbrlReader.Read(new HtmlParser().ParseDocument(MarkItDownConverter.ReadFiling(filing)));
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{filing.Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every filing the question names, in a stable (ordinal) order; empty when it names none. RagAnswerService
    /// searches each one separately - in one shared search, one company's chunks can crowd the other's out of the top-K.
    /// </summary>
    public string[] ResolveFilings(string question) => Registrations
        .Where(r => r.Names.Any(name => Regex.IsMatch(question, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)))
        .Select(r => r.Filing)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToArray();
}
