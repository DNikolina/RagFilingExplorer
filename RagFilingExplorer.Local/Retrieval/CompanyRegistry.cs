using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>One filing and the names a question can use for its company ("Microsoft", "MSFT").</summary>
internal sealed record CompanyRegistration(string Filing, IReadOnlyList<string> Names);

/// <summary>
/// Which filing(s) a question names, for the company filter. Built from each filing's own tagged cover facts - the
/// registrant name without its legal form ("MICROSOFT CORPORATION" -> "Microsoft", "Nasdaq, Inc." -> "Nasdaq") and
/// the common stock's trading symbol - replacing v1's hand-written QueryIntentResolver.CompanyToFiling table
/// (docs/Decision-Log.md, "registration from the cover facts"). That table was the manual step of onboarding: NFLX
/// ran every question unfiltered across all filings until it had an entry, and a hallucinated figure found it.
/// Every 10-K tags its cover page in inline XBRL, so a filing added to data/ now registers itself; one without a
/// registrant name fails at startup instead.
///
/// Names match as whole words, ignoring case: v1 matched substrings, which a future short name ("Meta" in
/// "metadata") would misread; on every question in both question files the two agree.
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
        List<string> names = [CoverFacts.ShortName(name)];
        if (CoverFacts.CommonStock(xbrl) is ({ } symbol, _))
        {
            names.Add(symbol);
        }

        return new CompanyRegistration(filing, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
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
