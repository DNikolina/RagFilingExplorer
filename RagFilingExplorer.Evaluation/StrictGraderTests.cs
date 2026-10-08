using RagFilingExplorer.Evaluation.Grading;

namespace RagFilingExplorer.Evaluation;

/// <summary>
/// The rules the .NET grader adds to the Python grader's, on answers from the Claude runs (shortened, structure kept).
/// The Python grader's own rules are GraderParityTests'.
/// </summary>
[TestFixture]
public class StrictGraderTests
{
    private static readonly IReadOnlyDictionary<string, ExpectedAnswer> Expected = ExpectedAnswer.LoadAll(RepoPaths.FindRoot(AppContext.BaseDirectory));

    private static StrictGrade Grade(string id, string answer) => StrictGrader.Grade(Expected[id], answer);

    // Claude answers the asked-for line, then names the lookalike to say why it's not that one.
    [TestCase("A10", "Microsoft paid $26,445 million in \"Common stock cash dividends paid\" for the year ended June 30, 2026 (Source: MSFT-10K-2026.html, PART II > Item 8). This differs from the \"Common stock cash dividends\" line in the stockholders' equity statement, which shows $27,034 million and reflects dividends charged to retained earnings rather than cash paid.", "27,034")]
    [TestCase("Q10", "Nasdaq's \"Comprehensive income\" was $2,113 million for the year ended December 31, 2025 (Source: NDAQ-10K-2025.html, PART IV > Financial Statements). The separate line \"Comprehensive income attributable to Nasdaq\" was $2,114 million, because it adds back $1 million of comprehensive loss attributable to noncontrolling interests.", "2,114")]
    public void Grade_LookalikeNamedAsTheContrast_Reliable(string id, string answer, string lookalike)
    {
        Assert.That(Grade(id, answer), Is.EqualTo(new StrictGrade("reliable", $"names {lookalike} as the contrast")));
    }

    // llama states the right figure first, then concludes with the lookalike - it answered $620 million.
    [Test]
    public void Grade_LookalikeStatedAsTheConclusion_StaysACheck()
    {
        StrictGrade grade = Grade("A16", "Nasdaq spent $616 million in total purchase price for repurchasing its common stock in 2025, and an additional $4 million of accrued excise tax is excluded from the total purchase price. Therefore, the total cash spent repurchasing its common stock in 2025 is $620 million.");

        Assert.That(grade, Is.EqualTo(new StrictGrade("check", "also states 620")));
    }

    // Sonnet gives both lines without choosing.
    [Test]
    public void Grade_HedgeBetweenTheLines_StaysACheck()
    {
        StrictGrade grade = Grade("V1", "The question is ambiguous between \"Comprehensive income\" and \"Comprehensive income attributable to Nasdaq,\" so I give both. The line \"Comprehensive income\" was $940 million for the year ended December 31, 2024. The line \"Comprehensive income attributable to Nasdaq\" was $942 million.");

        Assert.That(grade.Status, Is.EqualTo("check"));
    }

    [Test]
    public void Grade_LookalikeInTheFirstSentence_StaysACheck()
    {
        StrictGrade grade = Grade("Q10", "Nasdaq's comprehensive income was $2,113 million, or $2,114 million attributable to Nasdaq, for 2025.");

        Assert.That(grade.Status, Is.EqualTo("check"));
    }

    // A decline naming the periods the excerpts cover was "declines but states 31": a date's day counted as a figure.
    [TestCase("Q15", "The excerpts don't contain Nasdaq's total revenue for fiscal year 2030, since they report total revenues only for the years ended December 31, 2025, 2024 and 2023.")]
    [TestCase("V2", "The excerpts don't contain Microsoft's comprehensive income for 2023; they only cover the years ended June 30, 2026, 2025, and 2024.")]
    public void Grade_DeclineNamingDates_DeclineOk(string id, string answer)
    {
        Assert.That(Grade(id, answer), Is.EqualTo(new StrictGrade("decline-ok", "")));
    }

    // Figures from other periods are still figures: whether the decline is clean is a reader's call.
    [Test]
    public void Grade_DeclineStatingOtherPeriodsFigures_StaysACheck()
    {
        StrictGrade grade = Grade("H8", "The excerpts don't contain Oracle's total revenue for fiscal 2023. They only cover fiscal 2024 through 2026, and total revenues for those years were $52,961 million (2024), $57,399 million (2025), and $67,357 million (2026).");

        Assert.That(grade.Status, Is.EqualTo("check"));
    }
}
