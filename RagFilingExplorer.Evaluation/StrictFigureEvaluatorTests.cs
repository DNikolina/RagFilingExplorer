using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Evaluation.Evaluators;
using RagFilingExplorer.Evaluation.Grading;

namespace RagFilingExplorer.Evaluation;

/// <summary>The grader through Microsoft.Extensions.AI.Evaluation's interface - the rules themselves are GraderParityTests'.</summary>
[TestFixture]
public class StrictFigureEvaluatorTests
{
    // A10: the cash flow statement's $26,445M is the answer; the equity statement's declared $27,034M is the trap.
    private static readonly ExpectedAnswer A10 = new(
        "A10", "How much cash did Microsoft pay in common stock dividends in fiscal 2026?", "figure",
        Expect: ["26,445"], Unit: "million", Conflicts: ["27,034"], Traps: ["27,034", "27.0", "3.64"]);

    private static async Task<StringMetric> GradeAsync(string answer, ExpectedAnswer expected)
    {
        EvaluationResult result = await new StrictFigureEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, expected.Question)],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
            additionalContext: [new ExpectedAnswerContext(expected)]);
        return result.Get<StringMetric>(StrictFigureEvaluator.MetricName);
    }

    [Test]
    public async Task EvaluateAsync_ReliableAnswer_IsGoodAndNotFailed()
    {
        StringMetric metric = await GradeAsync("Microsoft paid $26,445 million in common stock cash dividends in fiscal 2026.", A10);

        Assert.That(metric.Value, Is.EqualTo("reliable"));
        Assert.That(metric.Interpretation!.Failed, Is.False);
        Assert.That(metric.Interpretation.Rating, Is.EqualTo(EvaluationRating.Good));
    }

    // The answer llama3.1:8b actually gave in eval/answer-side-norerank/.
    [Test]
    public async Task EvaluateAsync_TrapFigure_FailsWithTheGradersNote()
    {
        StringMetric metric = await GradeAsync("Microsoft paid $27,034 million in common stock cash dividends in fiscal 2026.", A10);

        Assert.That(metric.Value, Is.EqualTo("wrong"));
        Assert.That(metric.Metadata![StrictFigureEvaluator.GraderNoteKey], Is.EqualTo("trap 27,034"));
        Assert.That(StrictFigureEvaluator.GraderNote(metric), Is.EqualTo("trap 27,034"));
        Assert.That(metric.Reason, Is.EqualTo(StrictFigureEvaluator.Description), "the report shows the reason as \"What this measures?\"");
        Assert.That(metric.Interpretation!.Reason, Does.StartWith("States 27,034"));
        Assert.That(metric.Interpretation!.Failed, Is.True);
    }

    [Test]
    public void Readers_OlderStoredResults_ReadTheReason()
    {
        // Older stored runs keep the note and the figure trace as the metrics' reasons (the imported v1/v2 runs among them).
        StringMetric grade = new(StrictFigureEvaluator.MetricName, "wrong", "trap 27,034");
        StringMetric source = new(FigureSourceEvaluator.MetricName, "traced", "27,034: excerpt 1 (equity_statement, Item 8): \"...\"");

        Assert.That(StrictFigureEvaluator.GraderNote(grade), Is.EqualTo("trap 27,034"));
        Assert.That(FigureSourceEvaluator.TraceText(source), Is.EqualTo("27,034: excerpt 1 (equity_statement, Item 8): \"...\""));
    }

    [Test]
    public async Task Readers_DescriptionAsTheReason_IsNeverANoteOrATrace()
    {
        // A reliable grade has no note; a decline with nothing expected has no trace - their reason is the description only.
        StringMetric grade = await GradeAsync("Microsoft paid $26,445 million in common stock cash dividends in fiscal 2026.",
            new ExpectedAnswer("A10", "How much cash did Microsoft pay in common stock dividends in fiscal 2026?", "figure", Expect: ["26,445"], Unit: "million"));
        StringMetric source = new(FigureSourceEvaluator.MetricName, "no figure stated", FigureSourceEvaluator.Description);

        Assert.That(StrictFigureEvaluator.GraderNote(grade), Is.Empty);
        Assert.That(FigureSourceEvaluator.TraceText(source), Is.Empty);
    }

    [Test]
    public async Task GraderNote_V3BaselineStoredWithTheEarlierDescription_OnlyRealNotes()
    {
        // The v3 baseline was stored with the earlier description as each grade's reason; a grade without a
        // note (most reliable ones) must not get that reason back as its note.
        DiskBasedResultStore store = new(Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "eval", "v3-runs"));
        Dictionary<string, string> notes = [];
        await foreach (ScenarioRunResult result in store.ReadResultsAsync("structured-hybrid-v3-baseline"))
        {
            notes[result.ScenarioName] = StrictFigureEvaluator.GraderNote(result.EvaluationResult.Get<StringMetric>(StrictFigureEvaluator.MetricName));
        }

        Assert.That(notes.Values, Has.None.Contains("Whether the answer states"));
        Assert.That(notes["AnswerSide.A1"], Is.Empty);
        Assert.That(notes["HeldOut.H4"], Is.EqualTo("rounded, in billions"));
        Assert.That(notes["AnswerSide.A10"], Is.EqualTo("trap 27,034"));
    }

    [Test]
    public void EvaluateAsync_WithoutTheExpectedAnswer_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await new StrictFigureEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")], new ChatResponse(new ChatMessage(ChatRole.Assistant, "a"))));
    }
}
