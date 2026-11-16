using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
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
internal sealed record CompanyRegistration(string Filing, IReadOnlyList<string> Names, string? Context = null, string? Cik = null, string? FiscalYear = null)
{
    /// <summary>The <see cref="Names"/> a question must write as registered ("ARE", "Target"), because in lowercase they
    /// are ordinary words; every other name matches in any case.</summary>
    public IReadOnlyList<string> ExactCaseNames { get; init; } = [];
}

/// <summary>
/// Which filing(s) a question names, for the company filter. Built from each filing's own tagged cover facts - the
/// registrant name without its legal form ("MICROSOFT CORPORATION" -> "Microsoft", "Nasdaq, Inc." -> "Nasdaq") and
/// the trading symbol of each class of common stock (not notes or preferred stock). No hand-kept table to forget: a
/// company without an entry runs its questions unfiltered across every filing, which can produce a hallucinated
/// figure. Every 10-K tags its cover page in inline XBRL, so a filing added to data/ registers itself; one without a
/// registrant name fails at startup instead (docs/Decision-Log.md, "registration from the cover facts").
///
/// Names match as whole words - a substring match would misread a short name ("Meta" in "metadata") - and in any case
/// ("msft", "microsoft"), since questions are often typed without capitals. The exception is a name that is an ordinary
/// word in lowercase: the ticker ARE, COST or NOW, a company called Target. Matched in any case it would route "What
/// are Microsoft's revenues?" to that filing as well, so it matches only as registered. Which names those are is read
/// from the filings themselves (<see cref="IsOrdinaryWord"/>), so no word list is kept.
///
/// data/company-aliases.json, optional, adds names the cover facts can't give - a brand unlike the legal name
/// ("Google" for Alphabet): a ticker, then the extra names, each matched in any case. It only adds: a filing with no
/// entry still registers from its cover, so forgetting the file never leaves a company unfiltered.
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
        List<FileInfo> files = [.. filings];
        List<CompanyRegistration> registrations = [];
        HashSet<string> lowercaseWords = [];
        foreach (FileInfo filing in files)
        {
            (XbrlDocument xbrl, string text) = ReadPage(filing);
            registrations.Add(Register(filing.Name, xbrl));
            lowercaseWords.UnionWith(LowercaseWords(text));
        }

        EnsureOneFilingPerCompanyYear(registrations);

        // Decided on the names from the cover facts, before aliases join them: an alias is written by hand, to match in any case.
        registrations = [.. registrations.Select(r => r with { ExactCaseNames = [.. r.Names.Where(n => IsOrdinaryWord(n, lowercaseWords))] })];

        DirectoryInfo? dataDirectory = files.FirstOrDefault()?.Directory;

        return new(dataDirectory is null ? registrations : AddAliases(registrations, LoadAliases(dataDirectory)));
    }

    /// <summary>The aliases file's name, in the filings' folder.</summary>
    public const string AliasesFileName = "company-aliases.json";

    /// <summary>
    /// True for a one-word name that, written in lowercase, reads as an ordinary word: one the filings' own text uses
    /// (<paramref name="lowercaseWords"/> - "are", "cost", "target"; never "msft"), or any of one or two letters ("IT",
    /// "ON", "A"), too short to tell from a word or a stray letter whatever the filings hold.
    /// </summary>
    internal static bool IsOrdinaryWord(string name, IReadOnlySet<string> lowercaseWords) =>
        name.All(char.IsLetter) && (name.Length < 3 || lowercaseWords.Contains(name.ToLowerInvariant()));

    /// <summary>
    /// The words a text writes in lowercase, each standing alone between spaces (sentence punctuation aside). A word
    /// inside an address or a file name doesn't count - "www.microsoft.com", "ir@nasdaq.com", "msft-20260630.htm" - or
    /// every company's own name and ticker would read as an ordinary word.
    /// </summary>
    internal static IEnumerable<string> LowercaseWords(string text) => text
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(token => token.Trim('.', ',', ';', ':', '(', ')', '"', '\'', '\u201C', '\u201D', '\u2018', '\u2019', '?', '!'))
        .Where(word => word.Length > 0 && word.All(c => c is >= 'a' and <= 'z'));

    /// <summary>
    /// data/company-aliases.json as ticker (or any registered name) -> extra names; empty without the file. Comments and
    /// trailing commas are allowed. A file that isn't that shape fails naming it.
    /// </summary>
    internal static Dictionary<string, string[]> LoadAliases(DirectoryInfo dataDirectory)
    {
        string path = Path.Combine(dataDirectory.FullName, AliasesFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                    File.ReadAllText(path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{AliasesFileName} isn't a JSON object of ticker -> list of names, e.g. {{ \"GOOGL\": [\"Google\"] }}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The registrations with each alias added to its company's names - every filing of the company, when it has several
    /// years. Fails naming the entry when its key is no registered company, when an alias is empty, or when an alias and
    /// another company's name match each other as whole words: a question would then go to both.
    /// </summary>
    internal static List<CompanyRegistration> AddAliases(IReadOnlyList<CompanyRegistration> registrations, IReadOnlyDictionary<string, string[]> aliases)
    {
        List<CompanyRegistration> result = [.. registrations];
        foreach ((string key, string[] names) in aliases)
        {
            List<int> owners = [.. Enumerable.Range(0, registrations.Count).Where(i => registrations[i].Names.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase))];
            if (owners.Count == 0)
            {
                throw new InvalidOperationException(
                    $"{AliasesFileName}: '{key}' is no registered company - the filings register {string.Join(", ", registrations.SelectMany(r => r.Names).Distinct())}.");
            }

            foreach (string alias in names.Select(n => n?.Trim() ?? string.Empty))
            {
                if (alias.Length == 0)
                {
                    throw new InvalidOperationException($"{AliasesFileName}: '{key}' has an empty name.");
                }

                (CompanyRegistration Other, string Name)? collision = result
                    .Where((_, i) => !owners.Contains(i))
                    .SelectMany(other => other.Names.Select(name => (other, name)))
                    .Where(o => ContainsName(o.name, alias) || ContainsName(alias, o.name))
                    .Select(o => ((CompanyRegistration, string)?)o)
                    .FirstOrDefault();
                if (collision is { } hit)
                {
                    throw new InvalidOperationException(
                        $"{AliasesFileName}: '{alias}' ({key}) and '{hit.Name}' ({hit.Other.Filing}) match each other - a question naming one would go to both companies.");
                }
            }

            foreach (int owner in owners)
            {
                result[owner] = result[owner] with { Names = [.. result[owner].Names.Concat(names.Select(n => n.Trim())).Distinct(StringComparer.OrdinalIgnoreCase)] };
            }
        }

        return result;
    }

    private static bool ContainsName(string text, string name) => Regex.IsMatch(text, $@"\b{Regex.Escape(name)}\b", RegexOptions.IgnoreCase);

    public static CompanyRegistration Register(string filing, XbrlDocument xbrl)
    {
        string name = CoverFacts.RegistrantName(xbrl)
            ?? throw new InvalidOperationException($"{filing} tags no dei:EntityRegistrantName, so questions naming its company can't be routed to it.");

        string? form = CoverFacts.FormType(xbrl);
        if (form != "10-K")
        {
            throw new InvalidOperationException($"{filing} is {(form is null ? "untagged (no dei:DocumentType)" : $"a Form {form}")} - only 10-K annual reports can be indexed.");
        }

        List<string> names = [CoverFacts.ShortName(name), .. CoverFacts.CommonStocks(xbrl).Select(c => c.Symbol)];

        return new CompanyRegistration(
            filing,
            [.. names.Distinct(StringComparer.OrdinalIgnoreCase)],
            CoverFacts.EmbeddingContext(xbrl),
            CoverFacts.CentralIndexKey(xbrl),
            CoverFacts.FiscalYear(xbrl)
        );
    }

    /// <summary>Fails naming the filings when two are the same company's (by CIK, else by name) for the same fiscal year.</summary>
    internal static void EnsureOneFilingPerCompanyYear(IReadOnlyList<CompanyRegistration> registrations)
    {
        string[] duplicates = [.. registrations
            .GroupBy(r => (Company: r.Cik ?? r.Names[0], r.FiscalYear))
            .Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(r => r.Filing))} ({g.First().Names[0]}, fiscal year {g.Key.FiscalYear ?? "untagged"})")];

        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Two filings of one company for the same fiscal year: {string.Join("; ", duplicates)} - keep one of each in data/.");
        }
    }

    // The filing's XBRL and the text a reader sees - without the hidden XBRL header, whose member names ("msft:...") would
    // read as words. A file that isn't an inline XBRL filing, or one tagged with a format the reader doesn't know
    // (IxTransformations), fails inside the reader, which doesn't know the file's name.
    private static (XbrlDocument Xbrl, string Text) ReadPage(FileInfo filing)
    {
        try
        {
            IHtmlDocument page = new HtmlParser().ParseDocument(FilingFile.Read(filing));
            XbrlDocument xbrl = InlineXbrlReader.Read(page);
            foreach (IElement hidden in page.All.Where(e => e.LocalName is "ix:header" or "script" or "style").ToList())
            {
                hidden.Remove();
            }

            return (xbrl, page.Body?.TextContent ?? string.Empty);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NotSupportedException)
        {
            throw new InvalidOperationException($"{filing.Name}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every filing the question names, in a stable (ordinal) order; empty when it names none. RagAnswerService
    /// searches each one separately - in one shared search, one company's chunks can crowd the other's out of the top-K.
    /// </summary>
    public string[] ResolveFilings(string question) => [.. Registrations
        .Where(r => r.Names.Any(name => Regex.IsMatch(question, $@"\b{Regex.Escape(name)}\b",
            r.ExactCaseNames.Contains(name) ? RegexOptions.None : RegexOptions.IgnoreCase)))
        .Select(r => r.Filing)
        .Distinct()
        .Order(StringComparer.Ordinal)];
}
