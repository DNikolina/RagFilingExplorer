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
/// Which excerpt each figure in the answer came from. The excerpts are the ones the model was given - the top
/// <c>generationTopK</c> retrieved chunks, exactly the prompt's - and the figures are the ones the strict grader reads as
/// stated (<see cref="StrictGrader.StatedFigures"/>: years, citation text and the question's own numbers skipped), less
/// the day of a date ("December 31" - common in answers, and found in nearly every excerpt). For each
/// figure: every excerpt holding it, its statement type and section, and the line it sits on. A figure in two excerpts
/// lists both - which one the model read can't be known.
///
/// One string metric, "Figure source":
///   traced           every figure is in an excerpt
///   calculated       a figure is in none, and the question asks for a calculation (its chunk_expect lists two or more
///                    inputs and not the expected figure - H4, A21-A27): the result isn't printed anywhere
///   untraced         a figure is in none, on a question that doesn't ask for one - an invention to read (failed)
///   no figure stated a text answer ("Austin") or a decline - nothing to trace (a text answer isn't matched against
///                    the expected text)
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

    /// <summary>The figures traced: the strict grader's stated figures, less a date's day, distinct, in the answer's order.</summary>
    public static List<string> Figures(string answer, string question) =>
        StrictGrader.StatedFigures(MonthDayRegex().Replace(answer, m => m.Groups[1].Value), question).Distinct().ToList();

    /// <summary>Each distinct stated figure, in the answer's order, with the excerpts holding it.</summary>
    public static List<FigureSource> Trace(string answer, string question, IReadOnlyList<RetrievedExcerpt> excerpts)
    {
        List<FigureSource> sources = new();
        foreach (string figure in Figures(answer, question))
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

    /// <summary>A stated figure's excerpts in full: each one's position, statement type, section and line.</summary>
    private static string Detail(FigureSource source) =>
        source.Sightings.Count == 0
            ? "in none of the excerpts"
            : string.Join("; ", source.Sightings.Select(s =>
            {
                string line = Window(s.Line, source.Figure);
                string more = s.MoreLines > 0 ? $" (+{s.MoreLines} more line{(s.MoreLines > 1 ? "s" : "")})" : "";
                return $"excerpt {s.Position} ({s.Excerpt.StatementType}, {s.Excerpt.Heading}): \"{line}\"{more}";
            }));

    /// <summary>A stated figure in short, for the verdict: "27,034 from excerpt 1 (equity_statement)".</summary>
    private static string Summary(FigureSource source) =>
        source.Sightings.Count == 0
            ? $"{source.Figure} from no excerpt"
            : $"{source.Figure} from excerpt{(source.Sightings.Count > 1 ? "s" : "")} "
                + string.Join(", ", source.Sightings.Select(s => $"{s.Position} ({s.Excerpt.StatementType})"));

    /// <summary>Where an expected figure was: <see cref="Detail"/> in full (the metadata), <see cref="Summary"/> in short (the
    /// verdict), and whether it was on a line of an excerpt the model was given.</summary>
    internal sealed record ExpectedLocation(string Value, string Detail, string Summary, bool InAnExcerpt);

    /// <summary>
    /// For an answer the strict grade doesn't pass: where the expected answer was in the excerpts - "excerpt 3
    /// (cash_flow_statement, ...): "...paid — 2026: (26,445)"" - so a misreading reads as "stated this line, the answer was
    /// on that one"; "not printed as such - derived from ..." for a sum or ratio whose inputs were there; a retrieval miss
    /// only when what it comes from isn't in the excerpts. Empty for a passing answer or a negative (nothing expected).
    /// </summary>
    public static List<ExpectedLocation> ExpectedLocations(ExpectedAnswer expected, string answer, IReadOnlyList<RetrievedExcerpt> excerpts)
    {
        List<ExpectedLocation> locations = new();
        if (expected.Kind == "negative" || expected.Expect is not { Count: > 0 } expect || StrictGrader.Grade(expected, answer).Passed)
        {
            return locations;
        }

        foreach (string value in expect)
        {
            // A figure matches as the grader reads numbers; a fact ("Austin", "2025-2030") as text.
            List<string> numbers = StrictGrader.Numbers(value).ToList();
            bool isNumber = numbers.Count == 1 && numbers[0] == value;
            List<string> found = new();
            List<string> foundShort = new();
            for (int i = 0; i < excerpts.Count; i++)
            {
                List<string> holding = excerpts[i].Content.Split('\n')
                    .Where(line => isNumber ? StrictGrader.Numbers(line).Contains(value) : line.Contains(value, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (holding.Count > 0)
                {
                    string line = isNumber ? Window(holding[0].Trim(), value) : holding[0].Trim();
                    found.Add($"excerpt {i + 1} ({excerpts[i].StatementType}, {excerpts[i].Heading}): \"{line}\"");
                    foundShort.Add($"{i + 1} ({excerpts[i].StatementType})");
                }
            }

            if (found.Count > 0)
            {
                locations.Add(new ExpectedLocation(value, string.Join("; ", found),
                    $"the expected {value} was in excerpt{(found.Count > 1 ? "s" : "")} {string.Join(", ", foundShort)}", InAnExcerpt: true));
                continue;
            }

            // Not printed as stated: what the answer comes from (chunk_expect - a sum's or ratio's inputs, V3's first year)
            // decides between "derived from figures it was given" and a retrieval miss - as the answer rank reads it.
            // A marker that is the figure itself ("(26,445)") says nothing more.
            List<(string Marker, int? Position)> markers = (expected.ChunkExpect ?? [])
                .Where(m => !StrictGrader.Numbers(m).Contains(value))
                .Select(m => (m, FirstExcerptHolding(excerpts, m)))
                .ToList();
            string from = string.Join(", ", markers.Select(m => $"{m.Marker} ({(m.Position is { } p ? $"excerpt {p}" : "in no excerpt")})"));
            locations.Add(markers.Count > 0 && markers.All(m => m.Position is not null)
                ? new ExpectedLocation(value, $"not printed as such - derived from {from}", $"the expected {value} isn't printed - it's derived from {from}", InAnExcerpt: false)
                : markers.Count > 0
                    ? new ExpectedLocation(value, $"from {from} - retrieval missed it", $"the expected {value} comes from {from} - retrieval missed it", InAnExcerpt: false)
                    : new ExpectedLocation(value, "in none of the excerpts - retrieval missed it", $"the expected {value} was in none of the excerpts - retrieval missed it", InAnExcerpt: false));
        }

        return locations;
    }

    /// <summary>The expected answer's locations as text, one "Expected 26,445: ..." line each; null when there are none.</summary>
    public static string? ExpectedTrace(ExpectedAnswer expected, string answer, IReadOnlyList<RetrievedExcerpt> excerpts) =>
        ExpectedLocations(expected, answer, excerpts) is { Count: > 0 } locations
            ? string.Join("\n", locations.Select(l => $"Expected {l.Value}: {l.Detail}"))
            : null;

    private const string StatedKeyPrefix = "Stated ";
    private const string ExpectedKeyPrefix = "Expected ";

    /// <summary>
    /// A stored figure source's trace as text, one "Stated 27,034: ..." / "Expected 26,445: ..." line each - from its
    /// metadata, or its reason in older stored runs, which kept the trace there (Decision-Log, "Report layout").
    /// </summary>
    public static string TraceText(EvaluationMetric metric) =>
        metric.Metadata is { Count: > 0 } metadata
            ? string.Join("\n", metadata.Where(kv => kv.Key.StartsWith(StatedKeyPrefix, StringComparison.Ordinal) || kv.Key.StartsWith(ExpectedKeyPrefix, StringComparison.Ordinal))
                .Select(kv => $"{kv.Key}: {kv.Value}"))
            : metric.Reason is { } reason && reason != Description ? reason : "";

    /// <summary>What the metric measures - its reason, the report's "What this measures?".</summary>
    public const string Description =
        "Which of the excerpts the model was given hold each figure the answer states (their statement type, section and line), "
        + "and - for an answer that doesn't pass - where the expected figure was: on a line the model misread, derived from "
        + "figures it had (a calculation), or in no excerpt (a retrieval miss). A stated figure in no excerpt is flagged, unless "
        + "the question asks for a calculation. Deterministic - no model is asked.";

    /// <summary>The position (1-based) of the first excerpt holding <paramref name="text"/>, or null.</summary>
    private static int? FirstExcerptHolding(IReadOnlyList<RetrievedExcerpt> excerpts, string text)
    {
        for (int i = 0; i < excerpts.Count; i++)
        {
            if (excerpts[i].Content.Contains(text, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        return null;
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

        string answer = modelResponse.Text.Trim();
        List<RetrievedExcerpt> given = excerpts.Take(generationTopK).ToList();
        List<FigureSource> sources = Trace(answer, expected.Question, given);
        List<string> missing = sources.Where(s => s.Sightings.Count == 0).Select(s => s.Figure).ToList();
        List<ExpectedLocation> expectedAt = ExpectedLocations(expected, answer, given);

        // The verdict in one sentence is the interpretation's reason - the report's "Why this score?"; every figure's
        // excerpts and line are metadata - its Name/Value table. The metric's own reason, shown as "What this measures?",
        // is the description.
        string stated = string.Join(", ", sources.Select(Summary));
        string expectedPart = string.Join("; ", expectedAt.Select(l => l.Summary));
        string Joined(string first) => expectedPart.Length > 0 ? $"{first}; {expectedPart}." : $"{first}.";

        (string status, EvaluationMetricInterpretation interpretation) = sources.Count == 0
            ? ("no figure stated", new EvaluationMetricInterpretation(EvaluationRating.Inconclusive,
                reason: expectedPart.Length > 0 ? $"No figure stated - a text answer or a decline; {expectedPart}." : "No figure stated - a text answer or a decline."))
            : missing.Count == 0
                ? ("traced", new EvaluationMetricInterpretation(EvaluationRating.Good,
                    reason: expectedAt.Count > 0 && expectedAt.All(l => l.InAnExcerpt)
                        ? Joined($"A misreading - every figure is in an excerpt: {stated}")
                        : Joined($"Every figure is in an excerpt: {stated}")))
                : AsksForCalculation(expected)
                    ? ("calculated", new EvaluationMetricInterpretation(EvaluationRating.Good,
                        reason: Joined($"{string.Join(", ", missing)} in no excerpt - the question asks for a calculation; {stated}")))
                    : ("untraced", new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true,
                        reason: Joined($"{string.Join(", ", missing)} in none of the excerpts the model was given; {stated}")));

        StringMetric metric = new(MetricName, status, Description) { Interpretation = interpretation };
        foreach (FigureSource source in sources)
        {
            metric.AddOrUpdateMetadata(StatedKeyPrefix + source.Figure, Detail(source));
        }

        foreach (ExpectedLocation location in expectedAt)
        {
            metric.AddOrUpdateMetadata(ExpectedKeyPrefix + location.Value, location.Detail);
        }

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
