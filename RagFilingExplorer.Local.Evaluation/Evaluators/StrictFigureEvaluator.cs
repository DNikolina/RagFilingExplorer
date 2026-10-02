using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation.Evaluators;

/// <summary>The expected answer a <see cref="StrictFigureEvaluator"/> grades against, passed as additional context.</summary>
internal sealed class ExpectedAnswerContext(ExpectedAnswer expected)
    : EvaluationContext(ContextName, new TextContent($"{expected.Id}: {string.Join(", ", expected.Expect ?? [])} ({expected.Kind})"))
{
    public const string ContextName = "Expected answer";

    public ExpectedAnswer Expected { get; } = expected;
}

/// <summary>
/// The project's strict grading (<see cref="StrictGrader"/>, tools/grade_answers.py ported) as an evaluator: one string
/// metric, "Strict grade", whose value is the status (reliable, decline-ok, no-unit, wrong-unit, declined, wrong,
/// malformed, check) and whose reason is the grader's note. Deterministic - no model is asked.
/// </summary>
internal sealed class StrictFigureEvaluator : IEvaluator
{
    public const string MetricName = "Strict grade";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ExpectedAnswer expected = additionalContext?.OfType<ExpectedAnswerContext>().SingleOrDefault()?.Expected
            ?? throw new ArgumentException($"{nameof(StrictFigureEvaluator)} needs an {nameof(ExpectedAnswerContext)}.", nameof(additionalContext));

        StrictGrade grade = StrictGrader.Grade(expected, modelResponse.Text.Trim());
        StringMetric metric = new(MetricName, grade.Status, grade.Note.Length > 0 ? grade.Note : null)
        {
            Interpretation = grade.Status switch
            {
                "reliable" or "decline-ok" => new EvaluationMetricInterpretation(EvaluationRating.Good),
                "check" => new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, reason: "A reader decides: " + grade.Note),
                _ => new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: grade.Note.Length > 0 ? grade.Note : grade.Status),
            },
        };

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
