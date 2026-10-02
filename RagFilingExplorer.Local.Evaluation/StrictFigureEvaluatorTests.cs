using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation;

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
        Assert.That(metric.Reason, Is.EqualTo("trap 27,034"));
        Assert.That(metric.Interpretation!.Failed, Is.True);
    }

    [Test]
    public void EvaluateAsync_WithoutTheExpectedAnswer_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await new StrictFigureEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")], new ChatResponse(new ChatMessage(ChatRole.Assistant, "a"))));
    }
}
