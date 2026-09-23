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
    public void ResolveFiling_QuestionNamesExactlyOneCompany_ReturnsThatFiling(string question, string expectedFiling)
    {
        Assert.That(QueryIntentResolver.ResolveFiling(question), Is.EqualTo(expectedFiling));
    }

    [Test]
    public void ResolveFiling_QuestionNamesNoCompany_ReturnsNull()
    {
        Assert.That(QueryIntentResolver.ResolveFiling("What was the total revenue?"), Is.Null);
    }

    [Test]
    public void ResolveFiling_QuestionNamesMultipleCompanies_ReturnsNull()
    {
        Assert.That(QueryIntentResolver.ResolveFiling("Compare Microsoft and Oracle's revenue."), Is.Null);
    }

    [TestCase("What was the gross margin?", "income_statement")]
    [TestCase("What were total assets?", "balance_sheet")]
    [TestCase("What was cash flow from operating activities?", "cash_flow_statement")]
    [TestCase("What was total stockholders' equity?", "equity_statement")]
    [TestCase("What was other comprehensive income?", "comprehensive_income")]
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
}
