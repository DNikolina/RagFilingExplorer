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
/// malformed, check); its interpretation explains the grade in words (<see cref="StrictGradeExplanation"/>), and the
/// grader's note is its "Grader note" metadata. Deterministic - no model is asked.
///
/// The report shows a metric's interpretation reason under "Why this score?" and its own reason under "What this
/// measures?" - so the reason is <see cref="Description"/> (2026-10-05). Runs before that kept the note as the reason;
/// <see cref="GraderNote"/> reads either.
/// </summary>
internal sealed class StrictFigureEvaluator : IEvaluator
{
    public const string MetricName = "Strict grade";

    public const string GraderNoteKey = "Grader note";

    /// <summary>What the metric measures - its reason, the report's "What this measures?".</summary>
    public const string Description =
        "Whether the answer states the expected figure with its unit, from the line the question asks about: a lookalike line's "
        + "figure, a missing or wrong unit, or a decline where the filing has the figure all fail; where the filing doesn't say, "
        + "a decline passes. Deterministic - tools/grade_answers.py's rules, ported and held to it.";

    /// <summary>The grader's note on a stored grade - its metadata, or the reason in a run from before 2026-10-05.</summary>
    public static string GraderNote(EvaluationMetric metric) =>
        metric.Metadata is { } metadata && metadata.TryGetValue(GraderNoteKey, out string? note)
            ? note
            : metric.Reason is { } reason && reason != Description ? reason : "";

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

        string answer = modelResponse.Text.Trim();
        StrictGrade grade = StrictGrader.Grade(expected, answer);

        // The note stays the grader's (held to the Python grader), as metadata; the interpretation says it in words.
        string? explanation = StrictGradeExplanation.Explain(expected, grade, answer);
        StringMetric metric = new(MetricName, grade.Status, Description)
        {
            Interpretation = grade.Status switch
            {
                "reliable" or "decline-ok" => new EvaluationMetricInterpretation(EvaluationRating.Good, reason: explanation),
                "check" => new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, reason: explanation),
                _ => new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: explanation),
            },
        };
        if (grade.Note.Length > 0)
        {
            metric.AddOrUpdateMetadata(GraderNoteKey, grade.Note);
        }

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
