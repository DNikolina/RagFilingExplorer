using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Evaluation.Judging;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// The judge agreement report, offline. Report_Execution writes the agreement of a stored judge run -
/// [Explicit], because it writes into the repository:
///   Evaluation__JudgeExecution=structured-hybrid-v3-judge-equivalence (or JudgeExecution in evalsettings.json)
///   dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~JudgeAgreementTests.Report_Execution"
/// </summary>
[TestFixture]
public class JudgeAgreementTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private const string Equivalence = "Equivalence";

    private static JudgedAnswer Judged(string scenario, string grade, double? score, bool failed, string? error = null) =>
        new(scenario, "answer", grade, grade is "reliable" or "decline-ok", "traced",
            new Dictionary<string, JudgeVerdict> { [Equivalence] = new(score, failed, error) }, 20, 1);

    [Test]
    public void Report_ScoresAgainstTheStrictGrade_CountsAgreementAndListsDisagreements()
    {
        List<JudgedAnswer> answers =
        [
            Judged("AnswerSide.A1", "reliable", 5, false),
            Judged("AnswerSide.A10", "wrong", 4, false),   // a misreading the judge passes
            Judged("Main.R1", "decline-ok", 1, true),      // a routing test's decline: strict passes it
            Judged("Main.Q1", "reliable", null, true, "no score in the reply"),
        ];

        string report = JudgeAgreement.Report("x", answers, new Dictionary<string, string> { ["A10"] = "$26,445 million" });

        Assert.That(report, Does.Contain("Judge calls: 4, 1 min in all, 20 s per call"));
        Assert.That(report, Does.Contain("Reply not read as a score: 1"));
        Assert.That(report, Does.Contain("Score read as the prompt asks for it: 3; score taken from the reply's words: 0"));
        Assert.That(report, Does.Contain("  Main.Q1: no score in the reply"));
        Assert.That(report, Does.Contain("strict passed: 1:   1  2:   0  3:   0  4:   0  5:   1"));
        Assert.That(report, Does.Contain("Agreement with the strict grade at the library's verdict: 1/3 (judge passes a strict failure: 1, judge fails a strict pass: 1)"));
        Assert.That(report, Does.Contain("AnswerSide.A10: strict wrong, Equivalence 4 (passed)"));
        Assert.That(report, Does.Contain("expected: $26,445 million"));
        Assert.That(report, Does.Contain("Main.R1: strict decline-ok, Equivalence 1 (failed) [routing test: strict also passes a decline]"));
    }

    [Test]
    public async Task LoadAsync_V3Baseline_NoJudgeVerdicts()
    {
        List<JudgedAnswer> answers = await JudgeAgreement.LoadAsync(Path.Combine(Repo.FullName, "eval", "v3-runs"), "structured-hybrid-v3-baseline");

        Assert.That(answers, Has.Count.EqualTo(102));
        Assert.That(answers.All(a => a.Verdicts.Count == 0));
        Assert.That(answers.Count(a => a.StrictPassed), Is.EqualTo(84), "33 main + 31 held-out + 20 answer-side");
    }

    [Test]
    [Explicit("Writes into eval/v3-runs/.")]
    public async Task Report_Execution_WritesTheAgreement()
    {
        string execution = Running.EvaluationSettings.Load(Repo).JudgeExecution;
        Assert.That(execution, Is.Not.Empty, "Evaluation:JudgeExecution names the judge run.");
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");
        Dictionary<string, string> groundTruths = ExpectedAnswer.LoadAll(Repo).ToDictionary(kv => kv.Key, kv => JudgeSetup.GroundTruth(kv.Value));

        string report = JudgeAgreement.Report(execution, await JudgeAgreement.LoadAsync(storage, execution), groundTruths);

        File.WriteAllText(Path.Combine(storage, $"judge-{execution}.txt"), report);
        TestContext.Progress.WriteLine(report);
    }
}
