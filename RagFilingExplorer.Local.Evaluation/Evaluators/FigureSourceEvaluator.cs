using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation.Evaluators;

/// <summary>Where one stated figure sits: each excerpt holding it, by its position in the prompt (1-based).</summary>
internal sealed record FigureSource(string Figure, IReadOnlyList<FigureSighting> Sightings);

/// <summary>An excerpt holding a figure, and the first of its lines that does (with how many more do).</summary>
internal sealed record FigureSighting(int Position, RetrievedExcerpt Excerpt, string Line, int MoreLines);

/// <summary>
/// v3 step 5: which excerpt each figure in the answer came from. The excerpts are the ones the model was given - the top
/// <c>generationTopK</c> retrieved chunks, exactly the prompt's - and the figures are the ones the strict grader reads as
/// stated (<see cref="StrictGrader.StatedFigures"/>: years, citation text and the question's own numbers skipped), less
/// the day of a date ("December 31" - in 34 of the v3 baseline's answers, matching nearly every excerpt). For each
/// figure: every excerpt holding it, its statement type and section, and the line it sits on. A figure in two excerpts
/// lists both - which one the model read can't be known.
///
/// One string metric, "Figure source":
///   traced           every figure is in an excerpt
///   calculated       a figure is in none, and the question asks for a calculation (its chunk_expect lists two or more
///                    inputs and not the expected figure - H4, A21-A27): the result isn't printed anywhere
///   untraced         a figure is in none, on a question that doesn't ask for one - an invention to read (failed)
///   no figure stated a text answer ("Austin") or a decline - nothing to trace (user, 2026-10-05: not matched against the
///                    expected text)
/// Deterministic - no model is asked. A figure is matched as the grader tokenizes numbers, so "(26,445)" holds 26,445;
/// a figure the answer restates in other units ("$3.1 billion" for 3,074) isn't found.
/// </summary>
internal sealed partial class FigureSourceEvaluator(int generationTopK) : IEvaluator
{
    public const string MetricName = "Figure source";

    // A line longer than this is shown as a window around the figure: a narrative paragraph is often one line.
    private const int MaxLineLength = 160;

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    // The grader keeps these days as figures (grade_answers.py's rule, held by GraderParityTests); only the trace drops them.
    [GeneratedRegex(@"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex MonthDayRegex();

    /// <summary>A calculation question: two or more inputs in chunk_expect, none of them the expected figure.</summary>
    public static bool AsksForCalculation(ExpectedAnswer expected)
    {
        if (expected.ChunkExpect is not { Count: >= 2 } inputs || expected.Expect is not { Count: > 0 } expect)
        {
            return false;
        }

        HashSet<string> inputNumbers = inputs.SelectMany(StrictGrader.Numbers).ToHashSet();
        return expect.SelectMany(StrictGrader.Numbers).All(n => !inputNumbers.Contains(n));
    }

    /// <summary>Each distinct stated figure, in the answer's order, with the excerpts holding it.</summary>
    public static List<FigureSource> Trace(string answer, string question, IReadOnlyList<RetrievedExcerpt> excerpts)
    {
        List<FigureSource> sources = new();
        string withoutDays = MonthDayRegex().Replace(answer, m => m.Groups[1].Value);
        foreach (string figure in StrictGrader.StatedFigures(withoutDays, question).Distinct())
        {
            List<FigureSighting> sightings = new();
            for (int i = 0; i < excerpts.Count; i++)
            {
                List<string> lines = excerpts[i].Content.Split('\n')
                    .Where(line => StrictGrader.Numbers(line).Contains(figure))
                    .ToList();
                if (lines.Count > 0)
                {
                    sightings.Add(new FigureSighting(i + 1, excerpts[i], lines[0].Trim(), lines.Count - 1));
                }
            }

            sources.Add(new FigureSource(figure, sightings));
        }

        return sources;
    }

    /// <summary>The line, or for a long one about <see cref="MaxLineLength"/> characters centred on the figure.</summary>
    internal static string Window(string line, string figure)
    {
        if (line.Length <= MaxLineLength)
        {
            return line;
        }

        int at = Math.Max(0, StrictGrader.IndexOfNumber(line, figure));
        int start = Math.Clamp(at - MaxLineLength / 2, 0, line.Length - MaxLineLength);
        return (start > 0 ? "..." : "") + line.Substring(start, MaxLineLength) + (start + MaxLineLength < line.Length ? "..." : "");
    }

    private static string Describe(FigureSource source)
    {
        if (source.Sightings.Count == 0)
        {
            return $"{source.Figure}: in none of the excerpts";
        }

        return $"{source.Figure}: " + string.Join("; ", source.Sightings.Select(s =>
        {
            string line = Window(s.Line, source.Figure);
            string more = s.MoreLines > 0 ? $" (+{s.MoreLines} more line{(s.MoreLines > 1 ? "s" : "")})" : "";
            return $"excerpt {s.Position} ({s.Excerpt.StatementType}, {s.Excerpt.Heading}): \"{line}\"{more}";
        }));
    }

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        List<EvaluationContext> contexts = additionalContext?.ToList() ?? [];
        ExpectedAnswer expected = contexts.OfType<ExpectedAnswerContext>().SingleOrDefault()?.Expected
            ?? throw new ArgumentException($"{nameof(FigureSourceEvaluator)} needs an {nameof(ExpectedAnswerContext)}.", nameof(additionalContext));
        IReadOnlyList<RetrievedExcerpt> excerpts = contexts.OfType<RetrievedChunksContext>().SingleOrDefault()?.Excerpts
            ?? throw new ArgumentException($"{nameof(FigureSourceEvaluator)} needs a {nameof(RetrievedChunksContext)}.", nameof(additionalContext));

        List<FigureSource> sources = Trace(modelResponse.Text.Trim(), expected.Question, excerpts.Take(generationTopK).ToList());
        List<string> missing = sources.Where(s => s.Sightings.Count == 0).Select(s => s.Figure).ToList();

        StringMetric metric;
        if (sources.Count == 0)
        {
            metric = new StringMetric(MetricName, "no figure stated", "A text answer or a decline: nothing to trace.")
            {
                Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, reason: "Not traced."),
            };
        }
        else
        {
            string reason = string.Join("\n", sources.Select(Describe));
            if (missing.Count == 0)
            {
                metric = new StringMetric(MetricName, "traced", reason)
                {
                    Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Good, reason: "Every figure is in an excerpt."),
                };
            }
            else if (AsksForCalculation(expected))
            {
                metric = new StringMetric(MetricName, "calculated", reason)
                {
                    Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Good,
                        reason: $"{string.Join(", ", missing)} in no excerpt - the question asks for a calculation."),
                };
            }
            else
            {
                metric = new StringMetric(MetricName, "untraced", reason)
                {
                    Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true,
                        reason: $"{string.Join(", ", missing)} in none of the excerpts the model was given."),
                };
            }
        }

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
