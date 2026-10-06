using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// One filing, the names a question can use for its company ("Microsoft", "MSFT"), and its company line ("Microsoft
/// Corporation (MSFT), Form 10-K for fiscal year 2026." - <see cref="CoverFacts.EmbeddingContext"/>), which opens each of
/// its chunks for the reranker as it opens their embedding text.
/// </summary>
internal sealed record CompanyRegistration(string Filing, IReadOnlyList<string> Names, string? Context = null);

/// <summary>
/// Which filing(s) a question names, for the company filter. Built from each filing's own tagged cover facts - the
/// registrant name without its legal form ("MICROSOFT CORPORATION" -> "Microsoft", "Nasdaq, Inc." -> "Nasdaq") and
/// the trading symbol of each class of common stock (not notes or preferred stock). No hand-kept table to forget: a
/// company without an entry runs its questions unfiltered across every filing, which can produce a hallucinated
/// figure. Every 10-K tags its cover page in inline XBRL, so a filing added to data/ registers itself; one without a
/// registrant name fails at startup instead (docs/Decision-Log.md, "registration from the cover facts").
///
/// Names match as whole words, ignoring case - a substring match would misread a short name ("Meta" in "metadata").
/// </summary>
internal sealed class CompanyRegistry(IReadOnlyList<CompanyRegistration> registrations)
{
    public IReadOnlyList<CompanyRegistration> Registrations { get; } = registrations;

    /// <summary>Reads each filing's cover facts (about a second for the four here - each page is parsed).</summary>
    public static CompanyRegistry FromFilings(IEnumerable<FileInfo> filings) => new(filings.Select(filing =>
    {
        byte[] bytes = File.ReadAllBytes(filing.FullName);
        return Register(filing.Name, InlineXbrlReader.Read(new HtmlParser().ParseDocument(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes))));
    }).ToList());

    public static CompanyRegistration Register(string filing, XbrlDocument xbrl)
    {
        string name = CoverFacts.RegistrantName(xbrl)
            ?? throw new InvalidOperationException($"{filing} tags no dei:EntityRegistrantName, so questions naming its company can't be routed to it.");
        List<string> names = [CoverFacts.ShortName(name), .. CoverFacts.CommonStocks(xbrl).Select(c => c.Symbol)];

        return new CompanyRegistration(filing, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), CoverFacts.EmbeddingContext(xbrl));
    }

    /// <summary>
    /// Every filing the question names, in a stable (ordinal) order; empty when it names none. RagAnswerService
    /// searches each one separately - one shared search let "Compare Microsoft's and Oracle's total revenue" lose
    /// ORCL's revenue chunk to rank 10.
    /// </summary>
    public string[] ResolveFilings(string question) => Registrations
        .Where(r => r.Names.Any(name => Regex.IsMatch(question, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase)))
        .Select(r => r.Filing)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToArray();
}
