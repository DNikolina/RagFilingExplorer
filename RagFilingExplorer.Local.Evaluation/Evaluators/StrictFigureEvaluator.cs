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

internal static class EvaluationContexts
{
    /// <summary>The one <typeparamref name="T"/> among an evaluator's additional context; throws, naming the evaluator,
    /// when it wasn't given one.</summary>
    public static T Require<T>(IEnumerable<EvaluationContext>? additionalContext, string evaluator) where T : EvaluationContext =>
        additionalContext?.OfType<T>().SingleOrDefault()
        ?? throw new ArgumentException($"{evaluator} needs its {typeof(T).Name}.", nameof(additionalContext));
}

/// <summary>
/// The project's strict grading (<see cref="StrictGrader"/>, tools/grade_answers.py ported) as an evaluator: one string
/// metric, "Strict grade", whose value is the status (reliable, decline-ok, no-unit, wrong-unit, declined, wrong,
/// malformed, check); its interpretation explains the grade in words (<see cref="StrictGradeExplanation"/>), and the
/// grader's note is its "Grader note" metadata. Deterministic - no model is asked.
///
/// The report shows a metric's interpretation reason under "Why this score?" and its own reason under "What this
/// measures?" - so the reason is <see cref="Description"/>. Older stored runs kept the note as the reason;
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
        + "a decline passes. Deterministic - ported from tools/grade_answers.py and matched to its grade on all 1,029 answers "
        + "graded in v1 and v2 (GraderParityTests).";

    // The earlier wording of the description - still the reason of the runs stored with it, so it must not be read back
    // as a grader note.
    private const string EarlierDescription =
        "Whether the answer states the expected figure with its unit, from the line the question asks about: a lookalike line's "
        + "figure, a missing or wrong unit, or a decline where the filing has the figure all fail; where the filing doesn't say, "
        + "a decline passes. Deterministic - tools/grade_answers.py's rules, ported and held to it.";

    /// <summary>The grader's note on a stored grade - its metadata, or the reason in an older stored run.</summary>
    public static string GraderNote(EvaluationMetric metric) =>
        metric.Metadata is { } metadata && metadata.TryGetValue(GraderNoteKey, out string? note)
            ? note
            : metric.Reason is { } reason && reason is not (Description or EarlierDescription) ? reason : "";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ExpectedAnswer expected = EvaluationContexts.Require<ExpectedAnswerContext>(additionalContext, nameof(StrictFigureEvaluator)).Expected;

        string answer = modelResponse.Text.Trim();
        StrictGrade grade = StrictGrader.Grade(expected, answer);

        // The note stays the grader's (held to the Python grader), as metadata; the interpretation says it in words.
        string? explanation = StrictGradeExplanation.Explain(expected, grade, answer);
        StringMetric metric = new(MetricName, grade.Status, Description)
        {
            Interpretation = grade.Status switch
            {
                _ when grade.Passed => new EvaluationMetricInterpretation(EvaluationRating.Good, reason: explanation),
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
