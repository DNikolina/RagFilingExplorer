using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>The report's question tags, offline.</summary>
[TestFixture]
public class QuestionTagsTests
{
    private static readonly IReadOnlyDictionary<string, ExpectedAnswer> Expected = ExpectedAnswer.LoadAll(RepoPaths.FindRoot(AppContext.BaseDirectory));

    [Test]
    public void For_FigureWithTraps_KindTrapsCompanyAndStatement()
    {
        Assert.That(QuestionTags.For(Expected["A10"], ["MSFT-10K-2026.html"], "cash_flow_statement"),
            Is.EqualTo(new[] { "kind:figure", "has traps", "company:MSFT", "statement:cash_flow_statement" }));
    }

    [Test]
    public void For_Calculation_TaggedAsOne()
    {
        Assert.That(QuestionTags.For(Expected["A27"], ["ORCL-10K-2026.html"], "income_statement"), Does.Contain("calculation"));
        Assert.That(QuestionTags.For(Expected["A10"], ["MSFT-10K-2026.html"], null), Does.Not.Contain("calculation"));
    }

    [Test]
    public void For_TwoCompaniesOrNone_EachOrNone()
    {
        Assert.That(QuestionTags.For(Expected["Q14"], ["MSFT-10K-2026.html", "ORCL-10K-2026.html"], null),
            Is.EqualTo(new[] { "kind:figure", "company:MSFT", "company:ORCL", "statement:none" }));
        Assert.That(QuestionTags.For(Expected["Q14"], [], null), Does.Contain("company:none"));
    }
}
