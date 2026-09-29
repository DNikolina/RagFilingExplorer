using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Tests.Retrieval;

[TestFixture]
public class QueryIntentResolverTests
{
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
}
