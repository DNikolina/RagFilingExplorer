using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Retrieval;

/// <summary>
/// The company filter, registered from each filing's tagged cover facts. Every filing in data/ (offline) must register
/// a name, a ticker and its company line, with no name that would also route a question to another filing - checked
/// per filing, so a newly added one is tested as it is. The four filings v1 knew must register what its hand-written
/// table said, and every question in the three question files (main, held-out, answer-side) must resolve to the same
/// filings as it did under that table.
/// </summary>
[TestFixture]
public class CompanyRegistryTests
{
    // v1's QueryIntentResolver.CompanyToFiling, verbatim - kept here only as the reference the registry is checked against.
    private static readonly Dictionary<string, string> HandWrittenV1 = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft"] = "MSFT-10K-2026.html",
        ["MSFT"] = "MSFT-10K-2026.html",
        ["Oracle"] = "ORCL-10K-2026.html",
        ["ORCL"] = "ORCL-10K-2026.html",
        ["Nasdaq"] = "NDAQ-10K-2025.html",
        ["NDAQ"] = "NDAQ-10K-2025.html",
        ["Netflix"] = "NFLX-10K-2025.html",
        ["NFLX"] = "NFLX-10K-2025.html",
    };

    private static readonly CompanyRegistry Fixture = new(
    [
        new CompanyRegistration("MSFT-10K-2026.html", ["Microsoft", "MSFT"]),
        new CompanyRegistration("ORCL-10K-2026.html", ["Oracle", "ORCL"]),
        new CompanyRegistration("META-10K-2026.html", ["Meta", "META"]),
    ]);

    private static CompanyRegistry? _real;

    private static DirectoryInfo Repo => RepoPaths.FindRoot(TestContext.CurrentContext.TestDirectory);

    private static FileInfo[] DataFilings() =>
        new DirectoryInfo(Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "data")).GetFiles("*.html").OrderBy(f => f.Name).ToArray();

    private static CompanyRegistry Real => _real ??= CompanyRegistry.FromFilings(DataFilings());

    private static IEnumerable<string> DataFilingNames() => DataFilings().Select(f => f.Name);

    // What onboarding needs from a filing's cover, whichever filing it is: questions name the company by its name or its
    // ticker, and the company line opens each chunk's embedding text. A filing missing any of them registers badly in
    // silence - an unregistered NFLX once ran its questions across every filing and produced a hallucinated figure.
    [TestCaseSource(nameof(DataFilingNames))]
    public void Register_EveryFilingInData_HasItsNameATickerAndItsCompanyLine(string filing)
    {
        byte[] bytes = File.ReadAllBytes(Path.Combine(Repo.FullName, "data", filing));
        XbrlDocument xbrl = InlineXbrlReader.Read(new HtmlParser().ParseDocument(MarkItDownConverter.DetectEncoding(bytes).GetString(bytes)));

        CompanyRegistration registration = CompanyRegistry.Register(filing, xbrl);

        List<string> tickers = CoverFacts.CommonStocks(xbrl).Select(c => c.Symbol).ToList();
        Assert.That(tickers, Is.Not.Empty, "a common stock trading symbol");
        Assert.That(registration.Names, Is.SupersetOf(tickers.Prepend(CoverFacts.ShortName(CoverFacts.RegistrantName(xbrl)!))));
        Assert.That(registration.Context, Is.Not.Null.And.Contains(tickers[0]), "the company line");
    }

    // Names match as whole words, so one filing's name inside another's ("Bank" in "Bank of America") would send a
    // question about the second company to both.
    [Test]
    public void FromFilings_NoRegisteredName_AlsoMatchesAnotherFilingsName()
    {
        List<(string Name, string Filing)> names = Real.Registrations.SelectMany(r => r.Names.Select(n => (n, r.Filing))).ToList();

        List<string> collisions = names
            .SelectMany(a => names
                .Where(b => b.Filing != a.Filing && Regex.IsMatch(b.Name, $@"\b{Regex.Escape(a.Name)}\b", RegexOptions.IgnoreCase))
                .Select(b => $"'{a.Name}' ({a.Filing}) matches '{b.Name}' ({b.Filing})"))
            .ToList();

        Assert.That(collisions, Is.Empty);
    }

    // v1's four filings register exactly what its table said. A filing added since isn't in the table, so it's
    // checked by the tests above instead.
    [Test]
    public void FromFilings_FilingsV1Knew_RegisterWhatTheHandWrittenTableSaid()
    {
        HashSet<string> v1Filings = HandWrittenV1.Values.ToHashSet();
        Dictionary<string, string> registered = Real.Registrations
            .Where(r => v1Filings.Contains(r.Filing))
            .SelectMany(r => r.Names.Select(n => (Name: n, r.Filing)))
            .ToDictionary(p => p.Name, p => p.Filing, StringComparer.OrdinalIgnoreCase);

        Assert.That(registered, Is.EquivalentTo(HandWrittenV1));
    }

    // The verification this step was defined by: the filter each question gets is unchanged.
    [Test]
    public void ResolveFilings_EveryQuestionInTheQuestionFiles_SameFilingsAsTheHandWrittenTable()
    {
        string[] questions = new[] { "manual-questions.txt", "heldout-questions.txt", "answer-questions.txt" }
            .SelectMany(f => File.ReadAllLines(Path.Combine(Repo.FullName, "tools", f)))
            .Where(q => q.Trim().Length > 0)
            .ToArray();

        Assert.That(questions, Has.Length.EqualTo(102), "40 main + 35 held-out (H16-H35 appended 2026-09-30) + 27 answer-side (A1-A27, 2026-10-01)");
        foreach (string question in questions)
        {
            string[] v1 = HandWrittenV1.Where(kvp => question.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
            Assert.That(Real.ResolveFilings(question), Is.EqualTo(v1), question);
        }
    }

    [TestCase("What was Microsoft's total revenue?", "MSFT-10K-2026.html")]
    [TestCase("What was MSFT's total revenue?", "MSFT-10K-2026.html")]
    [TestCase("What was microsoft's total revenue?", "MSFT-10K-2026.html")] // case-insensitivity
    [TestCase("How much did Oracle spend on R&D?", "ORCL-10K-2026.html")]
    public void ResolveFilings_QuestionNamesExactlyOneCompany_ReturnsThatFiling(string question, string expectedFiling)
    {
        Assert.That(Fixture.ResolveFilings(question), Is.EqualTo(new[] { expectedFiling }));
    }

    [Test]
    public void ResolveFilings_QuestionNamesNoCompany_ReturnsEmpty()
    {
        Assert.That(Fixture.ResolveFilings("What was the total revenue?"), Is.Empty);
    }

    // Both names and tickers resolve, deduplicated, in stable ordinal order regardless of the order the
    // question names them in - RagAnswerService searches each one separately.
    [TestCase("Compare Microsoft and Oracle's revenue.")]
    [TestCase("Compare ORCL and MSFT revenue.")]
    [TestCase("Compare Oracle (ORCL) and Microsoft (MSFT) revenue.")]
    public void ResolveFilings_QuestionNamesMultipleCompanies_ReturnsEachFilingOnce(string question)
    {
        Assert.That(Fixture.ResolveFilings(question), Is.EqualTo(new[] { "MSFT-10K-2026.html", "ORCL-10K-2026.html" }));
    }

    // v1 matched substrings; a short registered name inside another word must not route the question.
    [Test]
    public void ResolveFilings_NameInsideAnotherWord_DoesNotMatch()
    {
        Assert.That(Fixture.ResolveFilings("Which metadata does Oracle's filing carry?"), Is.EqualTo(new[] { "ORCL-10K-2026.html" }));
    }

    [TestCase("Microsoft Corporation", "Microsoft")]
    [TestCase("Oracle Corporation", "Oracle")]
    [TestCase("Nasdaq, Inc.", "Nasdaq")]
    [TestCase("Netflix, Inc.", "Netflix")]
    [TestCase("Alphabet Inc.", "Alphabet")]
    [TestCase("Berkshire Hathaway Inc", "Berkshire Hathaway")]
    [TestCase("Corporation", "Corporation")] // never reduced to nothing
    // A leading "The" was kept ("The Coca-Cola"), so "Coca-Cola's revenue" matched no filing and ran unfiltered.
    [TestCase("The Coca-Cola Company", "Coca-Cola")]
    [TestCase("The Walt Disney Company", "Walt Disney")]
    public void ShortName_DropsTheLegalForm(string registrant, string shortName)
    {
        Assert.That(CoverFacts.ShortName(registrant), Is.EqualTo(shortName));
    }

    // Title-casing an all-capital value turned acronyms into "Kpmg LLP" and "At&T Inc."; ordinary words still change.
    [TestCase("MICROSOFT CORPORATION", "Microsoft Corporation")]
    [TestCase("KPMG LLP", "KPMG LLP")]
    [TestCase("AT&T INC.", "AT&T Inc.")]
    [TestCase("NEW YORK, NY", "New York, NY")]
    [TestCase("DELOITTE & TOUCHE LLP", "Deloitte & Touche LLP")]
    public void Clean_AllCapitalValue_TitleCaseKeepingAcronyms(string tagged, string cleaned)
    {
        Assert.That(CoverFacts.Clean(tagged), Is.EqualTo(cleaned));
    }

    [Test]
    public void Register_FilingWithoutARegistrantName_FailsLoudly()
    {
        XbrlDocument noCover = new(new Dictionary<string, XbrlContext>(), new Dictionary<string, XbrlUnit>(), []);

        Assert.Throws<InvalidOperationException>(() => CompanyRegistry.Register("AAPL-10K-2026.html", noCover));
    }
}
