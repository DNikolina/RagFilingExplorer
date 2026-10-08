using System.Text;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation.Judging;

/// <summary>One judge's verdict on one answer: its 1-5 score (null when no score could be read), whether the library
/// interprets the score as failed, any error it reported, and whether the score was recovered from the reply's words
/// (<see cref="JudgeSetup.RecoverScore"/>) rather than read by the library.</summary>
internal sealed record JudgeVerdict(double? Score, bool Failed, string? Error, bool Recovered = false);

/// <summary>One judged answer, read back from the result store.</summary>
internal sealed record JudgedAnswer(
    string Scenario, string Answer, string StrictGrade, bool StrictPassed, string FigureSource,
    IReadOnlyDictionary<string, JudgeVerdict> Verdicts, double JudgeSeconds, int JudgeCalls);

/// <summary>
/// Do the judges agree with the project's deterministic metrics? Equivalence against the strict grade
/// (passed = reliable or decline-ok), Groundedness against the figure source. Agreement is read at the library's own
/// verdict (a metric interpreted as failed), and every score is listed against the strict grade, so the threshold isn't
/// chosen after the fact.
/// </summary>
internal static class JudgeAgreement
{
    public static readonly IReadOnlyList<string> MetricNames =
        [EquivalenceEvaluator.EquivalenceMetricName, GroundednessEvaluator.GroundednessMetricName];

    private static readonly Dictionary<(string, int), bool> LibraryVerdicts = [];

    private static async Task<bool> LibraryFailsCachedAsync(string metricName, int score)
    {
        if (!LibraryVerdicts.TryGetValue((metricName, score), out bool failed))
        {
            failed = await JudgeSetup.LibraryFailsAsync(metricName, score);
            LibraryVerdicts[(metricName, score)] = failed;
        }

        return failed;
    }

    /// <summary>Every answer of the execution, with its judges' verdicts.</summary>
    public static async Task<List<JudgedAnswer>> LoadAsync(string storageRoot, string execution, CancellationToken cancellationToken = default)
    {
        List<JudgedAnswer> answers = [];
        foreach (ScenarioRunResult result in (await EvaluationRunner.LatestResultsAsync(storageRoot, execution, cancellationToken)).OrderBy(r => r.ScenarioName, StringComparer.Ordinal))
        {
            EvaluationResult evaluation = result.EvaluationResult;
            StringMetric grade = evaluation.Metrics.TryGetValue(StrictFigureEvaluator.MetricName, out EvaluationMetric? strict)
                ? (StringMetric)strict
                : throw new InvalidOperationException(
                    $"{execution} has no strict grade ({result.ScenarioName}) - the agreement needs a run with Graders: both.");
            Dictionary<string, JudgeVerdict> verdicts = [];
            foreach (string name in MetricNames)
            {
                if (evaluation.Metrics.TryGetValue(name, out EvaluationMetric? metric))
                {
                    string? error = metric.Diagnostics?.FirstOrDefault(d => d.Severity == EvaluationDiagnosticSeverity.Error)?.Message;
                    double? score = (metric as NumericMetric)?.Value;
                    // Scored by the library from the reply's words (ScoreOnlyEquivalenceEvaluator) - or, in a run stored
                    // without that evaluator, recovered here from the library's "failed to parse" error.
                    bool fromWords = metric.Metadata is { } metadata
                        && metadata.TryGetValue(ScoreOnlyEquivalenceEvaluator.TakenFromWordsKey, out string? taken) && taken == "yes";
                    verdicts[name] = score is null && JudgeSetup.RecoverScore(error) is int recovered
                        ? new JudgeVerdict(recovered, await LibraryFailsCachedAsync(name, recovered), error, Recovered: true)
                        : new JudgeVerdict(score, metric.Interpretation?.Failed ?? false, error, Recovered: fromWords);
                }
            }

            // The first turn is the app's answer (a cache hit in a judge run); the rest are the judges'.
            List<ChatTurnDetails> judgeTurns = result.ChatDetails?.TurnDetails?.Skip(1).ToList() ?? [];
            answers.Add(new JudgedAnswer(
                result.ScenarioName,
                result.ModelResponse.Text.Trim(),
                grade.Value ?? "",
                StrictGrade.IsPassing(grade.Value),
                evaluation.Metrics.TryGetValue(FigureSourceEvaluator.MetricName, out EvaluationMetric? source) ? ((StringMetric)source).Value ?? "" : "",
                verdicts,
                judgeTurns.Sum(t => t.Latency.TotalSeconds),
                judgeTurns.Count));
        }

        return answers;
    }

    /// <summary>The agreement as text.</summary>
    public static string Report(string execution, IReadOnlyList<JudgedAnswer> answers, IReadOnlyDictionary<string, string> groundTruths)
    {
        StringBuilder text = new();
        text.AppendLine($"Judge agreement for {execution}: {answers.Count} answers");
        int calls = answers.Sum(a => a.JudgeCalls);
        double seconds = answers.Sum(a => a.JudgeSeconds);
        text.AppendLine($"Judge calls: {calls}, {seconds / 60:F0} min in all, {(calls > 0 ? seconds / calls : 0):F0} s per call (0 s: from the cache)");

        foreach (string name in MetricNames.Where(n => answers.Any(a => a.Verdicts.ContainsKey(n))))
        {
            List<JudgedAnswer> judged = answers.Where(a => a.Verdicts.ContainsKey(name)).ToList();
            text.AppendLine();
            text.AppendLine($"== {name} ({judged.Count} answers)");

            List<JudgedAnswer> unread = judged.Where(a => a.Verdicts[name].Score is null).ToList();
            int recoveredCount = judged.Count(a => a.Verdicts[name].Recovered);
            text.AppendLine($"Score read as the prompt asks for it: {judged.Count - unread.Count - recoveredCount}; score taken from the reply's words: {recoveredCount}"
                + " (the library's own verdict for that score)");
            text.AppendLine($"Reply not read as a score: {unread.Count}");
            unread.ForEach(a => text.AppendLine($"  {a.Scenario}: {a.Verdicts[name].Error ?? "no score"}"));

            text.AppendLine("Score by strict grade (rows: strict passed / failed; columns: score 1-5):");
            foreach (bool passed in new[] { true, false })
            {
                List<JudgedAnswer> row = judged.Where(a => a.StrictPassed == passed && a.Verdicts[name].Score is not null).ToList();
                text.AppendLine($"  strict {(passed ? "passed" : "failed"),-6}: "
                    + string.Join("  ", Enumerable.Range(1, 5).Select(s => $"{s}: {row.Count(a => (int)Math.Round(a.Verdicts[name].Score!.Value) == s),3}")));
            }

            if (name == GroundednessEvaluator.GroundednessMetricName)
            {
                text.AppendLine("Score by figure source:");
                foreach (IGrouping<string, JudgedAnswer> group in judged.Where(a => a.Verdicts[name].Score is not null).GroupBy(a => a.FigureSource).OrderBy(g => g.Key))
                {
                    text.AppendLine($"  {group.Key,-16}: "
                        + string.Join("  ", Enumerable.Range(1, 5).Select(s => $"{s}: {group.Count(a => (int)Math.Round(a.Verdicts[name].Score!.Value) == s),3}")));
                }
            }

            List<JudgedAnswer> scored = judged.Where(a => a.Verdicts[name].Score is not null).ToList();
            int agree = scored.Count(a => a.StrictPassed != a.Verdicts[name].Failed);
            int libraryAgree = scored.Count(a => !a.Verdicts[name].Recovered && a.StrictPassed != a.Verdicts[name].Failed);
            text.AppendLine($"Agreement on library-read scores only: {libraryAgree}/{scored.Count(a => !a.Verdicts[name].Recovered)}");
            text.AppendLine($"Agreement with the strict grade at the library's verdict: {agree}/{scored.Count}"
                + $" (judge passes a strict failure: {scored.Count(a => !a.StrictPassed && !a.Verdicts[name].Failed)},"
                + $" judge fails a strict pass: {scored.Count(a => a.StrictPassed && a.Verdicts[name].Failed)})");

            text.AppendLine("Disagreements:");
            foreach (JudgedAnswer a in scored.Where(a => a.StrictPassed == a.Verdicts[name].Failed))
            {
                string id = a.Scenario.Split('.').Last();
                string routing = id.StartsWith('R') && a.Scenario.StartsWith("Main.", StringComparison.Ordinal) ? " [routing test: strict also passes a decline]" : "";
                string recovered = a.Verdicts[name].Recovered ? ", recovered" : "";
                text.AppendLine($"  {a.Scenario}: strict {a.StrictGrade}, {name} {a.Verdicts[name].Score} ({(a.Verdicts[name].Failed ? "failed" : "passed")}{recovered}){routing}");
                text.AppendLine($"    expected: {groundTruths.GetValueOrDefault(id, "?")}");
                text.AppendLine($"    answer:   {a.Answer.ReplaceLineEndings(" ")}");
            }
        }

        return text.ToString();
    }
}
