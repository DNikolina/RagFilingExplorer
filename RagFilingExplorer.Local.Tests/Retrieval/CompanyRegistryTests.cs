using RagFilingExplorer.Local.Retrieval;
using RagFilingExplorer.Local.Xbrl;

namespace RagFilingExplorer.Local.Tests.Retrieval;

/// <summary>
/// The company filter, registered from each filing's tagged cover facts. The four real filings (offline, from data/)
/// must register what v1's hand-written table said, and every question in the question files must resolve to the
/// same filings as it did under that table.
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

    private static CompanyRegistry Real => _real ??= CompanyRegistry.FromFilings(
        new DirectoryInfo(Path.Combine(Repo.FullName, "data")).GetFiles("*.html").OrderBy(f => f.Name));

    [Test]
    public void FromFilings_RealFilings_RegisterWhatTheHandWrittenTableSaid()
    {
        Dictionary<string, string> registered = Real.Registrations
            .SelectMany(r => r.Names.Select(n => (Name: n, r.Filing)))
            .ToDictionary(p => p.Name, p => p.Filing, StringComparer.OrdinalIgnoreCase);

        Assert.That(registered, Is.EquivalentTo(HandWrittenV1));
    }

    // The verification this step was defined by: the filter each question gets is unchanged.
    [Test]
    public void ResolveFilings_EveryQuestionInTheQuestionFiles_SameFilingsAsTheHandWrittenTable()
    {
        string[] questions = new[] { "manual-questions.txt", "heldout-questions.txt" }
            .SelectMany(f => File.ReadAllLines(Path.Combine(Repo.FullName, "tools", f)))
            .Where(q => q.Trim().Length > 0)
            .ToArray();

        Assert.That(questions, Has.Length.EqualTo(55));
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
    public void ShortName_DropsTheLegalForm(string registrant, string shortName)
    {
        Assert.That(CoverFacts.ShortName(registrant), Is.EqualTo(shortName));
    }

    [Test]
    public void Register_FilingWithoutARegistrantName_FailsLoudly()
    {
        XbrlDocument noCover = new(new Dictionary<string, XbrlContext>(), new Dictionary<string, XbrlUnit>(), []);

        Assert.Throws<InvalidOperationException>(() => CompanyRegistry.Register("AAPL-10K-2026.html", noCover));
    }
}
