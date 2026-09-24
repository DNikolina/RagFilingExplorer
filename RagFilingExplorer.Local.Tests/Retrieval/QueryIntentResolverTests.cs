using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Tests.Retrieval;

[TestFixture]
public class QueryIntentResolverTests
{
    [TestCase("What was Microsoft's total revenue?", "MSFT-10K-2026.html")]
    [TestCase("What was MSFT's total revenue?", "MSFT-10K-2026.html")]
    [TestCase("What was microsoft's total revenue?", "MSFT-10K-2026.html")] // case-insensitivity
    [TestCase("How much did Oracle spend on R&D?", "ORCL-10K-2026.html")]
    [TestCase("What are Nasdaq's total assets?", "NDAQ-10K-2025.html")]
    [TestCase("What was Netflix's total revenue?", "NFLX-10K-2025.html")]
    [TestCase("What was NFLX's total revenue?", "NFLX-10K-2025.html")]
    public void ResolveFilings_QuestionNamesExactlyOneCompany_ReturnsThatFiling(string question, string expectedFiling)
    {
        Assert.That(QueryIntentResolver.ResolveFilings(question), Is.EqualTo(new[] { expectedFiling }));
    }

    [Test]
    public void ResolveFilings_QuestionNamesNoCompany_ReturnsEmpty()
    {
        Assert.That(QueryIntentResolver.ResolveFilings("What was the total revenue?"), Is.Empty);
    }

    // Both names and tickers resolve, deduplicated, in stable ordinal order regardless of the order the
    // question names them in - RagAnswerService searches each one separately.
    [TestCase("Compare Microsoft and Oracle's revenue.")]
    [TestCase("Compare ORCL and MSFT revenue.")]
    [TestCase("Compare Oracle (ORCL) and Microsoft (MSFT) revenue.")]
    public void ResolveFilings_QuestionNamesMultipleCompanies_ReturnsEachFilingOnce(string question)
    {
        Assert.That(QueryIntentResolver.ResolveFilings(question), Is.EqualTo(new[] { "MSFT-10K-2026.html", "ORCL-10K-2026.html" }));
    }

    [TestCase("What was the gross margin?", "income_statement")]
    [TestCase("What were total assets?", "balance_sheet")]
    [TestCase("What was cash flow from operating activities?", "cash_flow_statement")]
    [TestCase("What was other comprehensive income?", "comprehensive_income")]
    // Period-end equity values route to the balance sheet's single total row, not the wide equity
    // roll-forward (where ORCL's closing balance ranked outside the top 5).
    [TestCase("What was total stockholders' equity?", "balance_sheet")]
    [TestCase("What was Oracle's total stockholders' equity?", "balance_sheet")]
    [TestCase("What was Nasdaq's total equity?", "balance_sheet")]
    [TestCase("What were total liabilities and stockholders' equity?", "balance_sheet")]
    // Questions about changes in equity still go to the equity statement - the longer keyword wins over
    // the "stockholders' equity" it contains.
    [TestCase("What did the statement of stockholders' equity show for dividends?", "equity_statement")]
    [TestCase("Summarize the changes in stockholders' equity.", "equity_statement")]
    // MSFT's own line label, which previously matched no keyword at all.
    [TestCase("What was Microsoft's net cash from operations?", "cash_flow_statement")]
    public void ResolveStatementType_QuestionMatchesExactlyOneType_ReturnsThatType(string question, string expectedType)
    {
        Assert.That(QueryIntentResolver.ResolveStatementType(question), Is.EqualTo(expectedType));
    }

    [Test]
    public void ResolveStatementType_QuestionMatchesNoKeywords_ReturnsNull()
    {
        Assert.That(QueryIntentResolver.ResolveStatementType("What does the company do?"), Is.Null);
    }

    [Test]
    public void ResolveStatementType_QuestionMatchesMultipleTypes_ReturnsNull()
    {
        Assert.That(QueryIntentResolver.ResolveStatementType("Compare revenue and total assets."), Is.Null);
    }

    [TestCase("Compare Microsoft's and Oracle's revenue.")]
    [TestCase("What is the difference between Oracle's and Nasdaq's total assets?")]
    [TestCase("What was the revenue trend over the last three years?")]
    [TestCase("Calculate the gross margin ratio.")]
    [TestCase("What was the year-over-year growth rate?")]
    public void RequiresSynthesis_MultiStepQuestion_ReturnsTrue(string question)
    {
        Assert.That(QueryIntentResolver.RequiresSynthesis(question), Is.True);
    }

    [TestCase("What was Microsoft's total revenue?")]
    [TestCase("What was total stockholders' equity?")]
    [TestCase("Who is Nasdaq's auditor?")]
    public void RequiresSynthesis_SingleFactLookup_ReturnsFalse(string question)
    {
        Assert.That(QueryIntentResolver.RequiresSynthesis(question), Is.False);
    }

    private static readonly string[] RegisteredFilings =
        ["MSFT-10K-2026.html", "ORCL-10K-2026.html", "NDAQ-10K-2025.html", "NFLX-10K-2025.html"];

    [Test]
    public void FindRegistrationProblems_EveryFilingRegistered_ReturnsNothing()
    {
        Assert.That(QueryIntentResolver.FindRegistrationProblems(RegisteredFilings), Is.Empty);
    }

    [Test]
    public void FindRegistrationProblems_UnregisteredFiling_IsReported()
    {
        List<string> problems = QueryIntentResolver.FindRegistrationProblems([.. RegisteredFilings, "AAPL-10K-2026.html"]);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain("AAPL-10K-2026.html").And.Contain("no entry"));
    }

    [Test]
    public void FindRegistrationProblems_RegisteredFilingMissingFromData_IsReported()
    {
        List<string> problems = QueryIntentResolver.FindRegistrationProblems(RegisteredFilings.Where(f => f != "NFLX-10K-2025.html"));

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.Contain("NFLX-10K-2025.html").And.Contain("isn't in data/"));
    }

    // Guards the real data/ folder, not a fixture: adding a filing without registering it (the NFLX
    // onboarding bug) now fails `dotnet test`, not just a startup warning.
    [Test]
    public void FindRegistrationProblems_ActualDataFolder_HasNoProblems()
    {
        DirectoryInfo dataDirectory = new(Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "data"));
        string[] filings = dataDirectory.GetFiles("*.html").Select(f => f.Name).ToArray();

        Assert.That(filings, Is.Not.Empty);
        Assert.That(QueryIntentResolver.FindRegistrationProblems(filings), Is.Empty);
    }
}
