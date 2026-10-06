using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// The variance comparison, offline: the classification, and the stored v3 baseline read back. Compare_Executions writes
/// the comparison of real executions - [Explicit], because it writes into the repository:
///   Evaluation__Compare=structured-hybrid-v3-baseline,structured-hybrid-v3-variance-1,structured-hybrid-v3-variance-2
///   Evaluation__CompareName=structured-hybrid-v3-variance
///   dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~VarianceComparisonTests.Compare_Executions"
/// </summary>
[TestFixture]
public class VarianceComparisonTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private const string Question = "How much cash did Nasdaq spend repurchasing its common stock in 2025?";

    private static StoredAnswer Answer(string execution, string answer, string grade = "reliable") =>
        new(execution, "AnswerSide.A16", Question, answer, grade, "traced");

    [Test]
    public void Classify_SameTextEverywhere_IsIdentical()
    {
        Assert.That(VarianceComparison.Classify([Answer("a", "Nasdaq spent $616 million."), Answer("b", "Nasdaq spent $616 million.")]),
            Is.EqualTo(AnswerVariation.Identical));
    }

    [Test]
    public void Classify_OtherWordsSameFigures_IsWording()
    {
        Assert.That(VarianceComparison.Classify([Answer("a", "Nasdaq spent $616 million."), Answer("b", "In 2025, Nasdaq spent $616 million on repurchases.")]),
            Is.EqualTo(AnswerVariation.Wording));
    }

    [Test]
    public void Classify_CurlyAgainstStraightApostrophe_IsIdentical()
    {
        // H2: v2's 5a run, logged in the console's code page, cites "MANAGEMENT'S"; the v3 baseline "MANAGEMENT’S".
        Assert.That(VarianceComparison.Classify([Answer("a", "19% (Source: Item 7. MANAGEMENT'S DISCUSSION)"), Answer("b", "19% (Source: Item 7. MANAGEMENT’S DISCUSSION)")]),
            Is.EqualTo(AnswerVariation.Identical));
    }

    [Test]
    public void Classify_DateOnlyInOne_IsWordingNotFigures()
    {
        // A25: v2's 5a answer said "at December 31, 2025", the v3 baseline's didn't - 31 isn't a figure.
        Assert.That(VarianceComparison.Classify([Answer("a", "Nasdaq spent $616 million at December 31, 2025."), Answer("b", "Nasdaq spent $616 million.")]),
            Is.EqualTo(AnswerVariation.Wording));
    }

    [Test]
    public void Classify_AddedFigureSameGrade_IsFigures()
    {
        Assert.That(VarianceComparison.Classify([Answer("a", "Nasdaq spent $616 million."), Answer("b", "Nasdaq spent $616 million, plus $4 million of excise tax.")]),
            Is.EqualTo(AnswerVariation.Figures));
    }

    [Test]
    public void Classify_DifferentGrade_IsGrade()
    {
        // A16: the same prompt answered $616M (reliable), then "$616M + $4M excise tax = $620M" (check).
        Assert.That(VarianceComparison.Classify(
            [Answer("a", "Nasdaq spent $616 million."), Answer("b", "$616 million plus $4 million is $620 million.", "check")]),
            Is.EqualTo(AnswerVariation.Grade));
    }

    // A judge-only run stores no strict grade; against a graded run that must not read as a grade change.
    [Test]
    public void Classify_RunWithoutStrictGrade_NoGradeChange()
    {
        Assert.That(VarianceComparison.Classify([Answer("a", "Nasdaq spent $616 million."), Answer("b", "Nasdaq spent $616 million.", VarianceComparison.NoGrade)]),
            Is.EqualTo(AnswerVariation.Identical));
        Assert.That(VarianceComparison.Report(["a", "b"], [Answer("a", "x"), Answer("b", "x", VarianceComparison.NoGrade)]),
            Does.Contain("a vs b: 1/1 / no strict grade"));
    }

    [Test]
    public void Report_OnlyQuestionsInEveryExecution_AreCompared()
    {
        List<StoredAnswer> answers =
        [
            Answer("a", "Nasdaq spent $616 million."),
            Answer("b", "Nasdaq spent $620 million.", "check"),
            new("a", "Main.Q1", "q", "only in a", "reliable", "traced"),
        ];

        string report = VarianceComparison.Report(["a", "b"], answers);

        Assert.That(report, Does.Contain("1 questions answered in every execution."));
        Assert.That(report, Does.Contain("All: identical 0  wording 0  figures 0  grade 1"));
        Assert.That(report, Does.Contain("a vs b: 0/1 / 0/1"));
        Assert.That(report, Does.Not.Contain("Main.Q1"));
    }

    [Test]
    public async Task LoadAsync_V3Baseline_EveryAnswerWithItsGradeAndFigureSource()
    {
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");

        List<StoredAnswer> answers = await VarianceComparison.LoadAsync(storage, ["structured-hybrid-v3-baseline"]);

        Assert.That(answers, Has.Count.EqualTo(102));
        StoredAnswer a10 = answers.Single(a => a.Scenario == "AnswerSide.A10");
        Assert.That(a10.Question, Does.StartWith("How much cash did Microsoft pay in common stock dividends"));
        Assert.That(a10.Grade, Is.EqualTo("wrong"));
        Assert.That(a10.FigureSource, Is.EqualTo("traced"));
        Assert.That(a10.Figures, Is.EqualTo(new[] { "27,034" }));
        Assert.That(VarianceComparison.Classify([a10, a10]), Is.EqualTo(AnswerVariation.Identical));
    }

    [Test]
    [Explicit("Writes into eval/v3-runs/.")]
    public async Task Compare_Executions_WritesTheReport()
    {
        EvaluationSettings settings = EvaluationSettings.Load(Repo);
        List<string> executions = settings.CompareExecutions.ToList();
        Assert.That(executions, Has.Count.GreaterThanOrEqualTo(2), "Evaluation:Compare needs two or more execution names, comma-separated.");
        string name = settings.CompareName;
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");

        string report = VarianceComparison.Report(executions, await VarianceComparison.LoadAsync(storage, executions));

        File.WriteAllText(Path.Combine(storage, $"{name}.txt"), report);
        TestContext.Progress.WriteLine(report);
    }
}
