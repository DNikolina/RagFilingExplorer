using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.Xbrl;

namespace RagFilingExplorer.Tests.Retrieval;

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

    private string _tempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CompanyRegistryTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDir, recursive: true);

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
        XbrlDocument xbrl = InlineXbrlReader.Read(new HtmlParser().ParseDocument(FilingFile.Read(new FileInfo(Path.Combine(Repo.FullName, "data", filing)))));

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

    // What the registry has to keep: the filter each question gets is the one v1's table gave it.
    [Test]
    public void ResolveFilings_EveryQuestionInTheQuestionFiles_SameFilingsAsTheHandWrittenTable()
    {
        string[] questions = new[] { "manual-questions.txt", "heldout-questions.txt", "answer-questions.txt" }
            .SelectMany(f => File.ReadAllLines(Path.Combine(Repo.FullName, "tools", f)))
            .Where(q => q.Trim().Length > 0)
            .ToArray();

        Assert.That(questions, Has.Length.EqualTo(102), "40 main + 35 held-out + 27 answer-side");
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

    // With several files in data/, an error that doesn't name the file leaves the user to find it.
    [Test]
    public void FromFilings_FileWithoutInlineXbrl_FailsNamingTheFile()
    {
        string page = Path.Combine(_tempDir, "notes.html");
        File.WriteAllText(page, "<html><body><p>Not a filing.</p></body></html>");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CompanyRegistry.FromFilings([new FileInfo(page)]))!;

        Assert.That(ex.Message, Does.StartWith("notes.html: ").And.Contain("not an inline XBRL filing"));
    }

    [TestCase("10-Q")]
    [TestCase("10-K/A")]
    [TestCase("8-K")]
    public void Register_FormOtherThan10K_FailsNamingTheForm(string form)
    {
        XbrlDocument cover = Cover(("dei:EntityRegistrantName", "Apple Inc."), ("dei:DocumentType", form));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => CompanyRegistry.Register("AAPL.html", cover))!;

        Assert.That(ex.Message, Does.Contain($"AAPL.html is a Form {form}"));
    }

    [Test]
    public void Register_FormUntagged_Fails()
    {
        XbrlDocument cover = Cover(("dei:EntityRegistrantName", "Apple Inc."));

        Assert.That(() => CompanyRegistry.Register("AAPL.html", cover), Throws.InvalidOperationException.With.Message.Contains("no dei:DocumentType"));
    }

    [Test]
    public void Register_10K_RecordsItsCikAndFiscalYear()
    {
        XbrlDocument cover = Cover(("dei:EntityRegistrantName", "Apple Inc."), ("dei:DocumentType", "10-K"),
            ("dei:EntityCentralIndexKey", "0000320193"), ("dei:DocumentFiscalYearFocus", "2026"));

        CompanyRegistration registration = CompanyRegistry.Register("AAPL.html", cover);

        Assert.That((registration.Cik, registration.FiscalYear), Is.EqualTo(("0000320193", "2026")));
    }

    [Test]
    public void EnsureOneFilingPerCompanyYear_SameCompanySameYear_FailsNamingBothFilings()
    {
        CompanyRegistration[] registrations =
        [
            new("MSFT-10K-2026.html", ["Microsoft", "MSFT"], Cik: "0000789019", FiscalYear: "2026"),
            new("MSFT-copy.html", ["Microsoft", "MSFT"], Cik: "0000789019", FiscalYear: "2026"),
        ];

        Assert.That(() => CompanyRegistry.EnsureOneFilingPerCompanyYear(registrations),
            Throws.InvalidOperationException.With.Message.Contains("MSFT-10K-2026.html and MSFT-copy.html"));
    }

    // Another year of the same company is a filing to keep, not a duplicate.
    [Test]
    public void EnsureOneFilingPerCompanyYear_SameCompanyDifferentYears_Passes()
    {
        CompanyRegistration[] registrations =
        [
            new("MSFT-10K-2025.html", ["Microsoft", "MSFT"], Cik: "0000789019", FiscalYear: "2025"),
            new("MSFT-10K-2026.html", ["Microsoft", "MSFT"], Cik: "0000789019", FiscalYear: "2026"),
        ];

        Assert.That(() => CompanyRegistry.EnsureOneFilingPerCompanyYear(registrations), Throws.Nothing);
    }

    // A ticker that is an English word, matched in any case, sent "What are Microsoft's revenues?" to that filing too:
    // each named company then gets half of the excerpts.
    [Test]
    public void ResolveFilings_NameThatIsAnOrdinaryWord_MatchesOnlyAsWritten()
    {
        CompanyRegistry registry = new(
        [
            new CompanyRegistration("MSFT-10K-2026.html", ["Microsoft", "MSFT"]),
            new CompanyRegistration("ARE-10K-2026.html", ["Alexandria Real Estate Equities", "ARE"]) { ExactCaseNames = ["ARE"] },
        ]);

        Assert.That(registry.ResolveFilings("What are Microsoft's revenues?"), Is.EqualTo(new[] { "MSFT-10K-2026.html" }));
        Assert.That(registry.ResolveFilings("What was ARE's rental revenue?"), Is.EqualTo(new[] { "ARE-10K-2026.html" }));
        Assert.That(registry.ResolveFilings("what was alexandria real estate equities' rental revenue?"), Is.EqualTo(new[] { "ARE-10K-2026.html" }));
    }

    // Questions are typed without capitals: none of the four filings' names or tickers is an ordinary word, so each
    // still matches in lowercase. Their own addresses ("www.microsoft.com") must not make them read as one.
    [TestCase("microsoft", "MSFT-10K-2026.html")]
    [TestCase("msft", "MSFT-10K-2026.html")]
    [TestCase("oracle", "ORCL-10K-2026.html")]
    [TestCase("orcl", "ORCL-10K-2026.html")]
    [TestCase("nasdaq", "NDAQ-10K-2025.html")]
    [TestCase("ndaq", "NDAQ-10K-2025.html")]
    [TestCase("netflix", "NFLX-10K-2025.html")]
    [TestCase("nflx", "NFLX-10K-2025.html")]
    public void FromFilings_FilingsInData_EveryNameMatchesInLowercase(string name, string filing)
    {
        Assert.That(Real.Registrations.SelectMany(r => r.ExactCaseNames), Is.Empty);
        Assert.That(Real.ResolveFilings($"what was {name}'s total revenue?"), Is.EqualTo(new[] { filing }));
    }

    [Test]
    public void FromFilings_TickerTheFilingsUseAsAWord_MatchesOnlyAsWritten()
    {
        FileInfo costco = WriteFiling("COST-10K-2026.html", "Costco Wholesale Corporation", "COST", "0000909832", "The cost of merchandise rose.");
        FileInfo acme = WriteFiling("ACME-10K-2026.html", "Acme Corporation", "ACME", "0000000001", "See www.acme.com for more.");

        CompanyRegistry registry = CompanyRegistry.FromFilings([costco, acme]);

        Assert.That(registry.Registrations.Single(r => r.Filing == costco.Name).ExactCaseNames, Is.EqualTo(new[] { "COST" }));
        Assert.That(registry.ResolveFilings("what was acme's cost of revenue?"), Is.EqualTo(new[] { acme.Name }));
        Assert.That(registry.ResolveFilings("What was COST's revenue?"), Is.EqualTo(new[] { costco.Name }));
        Assert.That(registry.ResolveFilings("what was costco wholesale's revenue?"), Is.EqualTo(new[] { costco.Name }));
    }

    [Test]
    public void LowercaseWords_WordsInsideAddressesAndFileNames_AreNotWords()
    {
        string text = "Our cost (net) rose; see www.microsoft.com, ir@nasdaq.com and msft-20260630.htm. Cost of revenue fell\u00A0now.";

        Assert.That(CompanyRegistry.LowercaseWords(text), Is.EquivalentTo(new[] { "cost", "net", "rose", "see", "and", "of", "revenue", "fell", "now" }));
    }

    [TestCase("COST", true)]
    [TestCase("MSFT", false)]
    [TestCase("IT", true)] // two letters: exact whatever the filings hold
    [TestCase("GE", true)]
    [TestCase("BRK.B", false)] // not one word of letters
    [TestCase("Walt Disney", false)]
    public void IsOrdinaryWord_OneWordNames_ByTheFilingsWordsAndLength(string name, bool expected)
    {
        Assert.That(CompanyRegistry.IsOrdinaryWord(name, new HashSet<string> { "cost", "walt" }), Is.EqualTo(expected));
    }

    [Test]
    public void FromFilings_AliasesFile_AddsNamesMatchedInAnyCase()
    {
        FileInfo alphabet = WriteFiling("GOOGL-10K-2026.html", "Alphabet Inc.", "GOOGL", "0001652044", "We build products.");
        File.WriteAllText(Path.Combine(_tempDir, CompanyRegistry.AliasesFileName), """
            {
              // The brand questions use.
              "GOOGL": ["Google"],
            }
            """);

        CompanyRegistry registry = CompanyRegistry.FromFilings([alphabet]);

        Assert.That(registry.Registrations.Single().Names, Is.EqualTo(new[] { "Alphabet", "GOOGL", "Google" }));
        Assert.That(registry.ResolveFilings("what was google's revenue?"), Is.EqualTo(new[] { alphabet.Name }));
    }

    [Test]
    public void FromFilings_AliasesFileMalformed_FailsNamingTheFile()
    {
        FileInfo alphabet = WriteFiling("GOOGL-10K-2026.html", "Alphabet Inc.", "GOOGL", "0001652044", "We build products.");
        File.WriteAllText(Path.Combine(_tempDir, CompanyRegistry.AliasesFileName), """{ "GOOGL": "Google" }""");

        Assert.That(() => CompanyRegistry.FromFilings([alphabet]),
            Throws.InvalidOperationException.With.Message.StartsWith("company-aliases.json isn't a JSON object of ticker -> list of names"));
    }

    [Test]
    public void AddAliases_KeyThatIsNoRegisteredCompany_FailsNamingIt()
    {
        Assert.That(() => CompanyRegistry.AddAliases(Fixture.Registrations, new Dictionary<string, string[]> { ["GOOGL"] = ["Google"] }),
            Throws.InvalidOperationException.With.Message.Contains("'GOOGL' is no registered company"));
    }

    // "Meta" as Oracle's alias would send every Meta question to Oracle's filing too.
    [TestCase("Meta")]
    [TestCase("Meta Platforms")]
    public void AddAliases_AliasMatchingAnotherCompanysName_Fails(string alias)
    {
        Assert.That(() => CompanyRegistry.AddAliases(Fixture.Registrations, new Dictionary<string, string[]> { ["ORCL"] = [alias] }),
            Throws.InvalidOperationException.With.Message.Contains("match each other"));
    }

    [Test]
    public void AddAliases_CompanyWithTwoYears_BothFilingsGetTheAlias()
    {
        CompanyRegistration[] registrations =
        [
            new("MSFT-10K-2025.html", ["Microsoft", "MSFT"]),
            new("MSFT-10K-2026.html", ["Microsoft", "MSFT"]),
        ];

        CompanyRegistry registry = new(CompanyRegistry.AddAliases(registrations, new Dictionary<string, string[]> { ["msft"] = ["Redmond"] }));

        Assert.That(registry.ResolveFilings("What was Redmond's revenue?"), Is.EqualTo(new[] { "MSFT-10K-2025.html", "MSFT-10K-2026.html" }));
    }

    // A 10-K reduced to its tagged cover facts and one sentence of text.
    private FileInfo WriteFiling(string fileName, string registrant, string ticker, string cik, string text)
    {
        static string Fact(string concept, string value) => $"<ix:nonNumeric name=\"dei:{concept}\" contextRef=\"c1\">{value}</ix:nonNumeric>";
        string path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, $"""
            <html><body>
            <div style="display:none"><ix:header><ix:resources><xbrli:context id="c1"></xbrli:context></ix:resources></ix:header></div>
            <p>{Fact("EntityRegistrantName", registrant)} {Fact("DocumentType", "10-K")} {Fact("EntityCentralIndexKey", cik)}
            {Fact("DocumentFiscalYearFocus", "2026")} {Fact("Security12bTitle", "Common Stock")} {Fact("TradingSymbol", ticker)}</p>
            <p>{text}</p>
            </body></html>
            """);

        return new FileInfo(path);
    }

    // A cover page holding only the given dei: facts, each in its own context.
    private static XbrlDocument Cover(params (string Concept, string Text)[] facts) => new(
        new Dictionary<string, XbrlContext>(),
        new Dictionary<string, XbrlUnit>(),
        facts.Select((f, i) => new XbrlFact(null, f.Concept, $"c{i}", null, false, false, null, 0, null, false, f.Text, null, f.Text, false, [])).ToList());
}
