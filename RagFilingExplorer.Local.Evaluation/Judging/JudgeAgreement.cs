using System.Text;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Local.Evaluation.Evaluators;

namespace RagFilingExplorer.Local.Evaluation.Judging;

/// <summary>One judge's verdict on one answer: its 1-5 score (null when its reply couldn't be read), whether the library
/// interprets the score as failed, and any error it reported.</summary>
internal sealed record JudgeVerdict(double? Score, bool Failed, string? Error);

/// <summary>One judged answer, read back from the result store.</summary>
internal sealed record JudgedAnswer(
    string Scenario, string Answer, string StrictGrade, bool StrictPassed, string FigureSource,
    IReadOnlyDictionary<string, JudgeVerdict> Verdicts, double JudgeSeconds, int JudgeCalls);

/// <summary>
/// Step 6's measure: do the judges agree with the project's deterministic metrics? Equivalence against the strict grade
/// (passed = reliable or decline-ok), Groundedness against the figure source. Agreement is read at the library's own
/// verdict (a metric interpreted as failed), and every score is listed against the strict grade, so the threshold isn't
/// chosen after the fact.
/// </summary>
internal static class JudgeAgreement
{
    public static readonly IReadOnlyList<string> MetricNames =
        [EquivalenceEvaluator.EquivalenceMetricName, GroundednessEvaluator.GroundednessMetricName];

    /// <summary>Every answer of the execution, with its judges' verdicts.</summary>
    public static async Task<List<JudgedAnswer>> LoadAsync(string storageRoot, string execution, CancellationToken cancellationToken = default)
    {
        DiskBasedResultStore store = new(storageRoot);
        Dictionary<string, ScenarioRunResult> latest = new();
        await foreach (ScenarioRunResult result in store.ReadResultsAsync(execution, cancellationToken: cancellationToken))
        {
            if (!latest.TryGetValue(result.ScenarioName, out ScenarioRunResult? seen) || result.CreationTime > seen.CreationTime)
            {
                latest[result.ScenarioName] = result;
            }
        }

        if (latest.Count == 0)
        {
            throw new InvalidOperationException($"No stored results for execution {execution} under {storageRoot}.");
        }

        List<JudgedAnswer> answers = new();
        foreach (ScenarioRunResult result in latest.Values.OrderBy(r => r.ScenarioName, StringComparer.Ordinal))
        {
            EvaluationResult evaluation = result.EvaluationResult;
            StringMetric grade = evaluation.Get<StringMetric>(StrictFigureEvaluator.MetricName);
            Dictionary<string, JudgeVerdict> verdicts = new();
            foreach (string name in MetricNames)
            {
                if (evaluation.Metrics.TryGetValue(name, out EvaluationMetric? metric))
                {
                    string? error = metric.Diagnostics?.FirstOrDefault(d => d.Severity == EvaluationDiagnosticSeverity.Error)?.Message;
                    verdicts[name] = new JudgeVerdict((metric as NumericMetric)?.Value, metric.Interpretation?.Failed ?? false, error);
                }
            }

            // The first turn is the app's answer (a cache hit in a judge run); the rest are the judges'.
            List<ChatTurnDetails> judgeTurns = result.ChatDetails?.TurnDetails?.Skip(1).ToList() ?? [];
            answers.Add(new JudgedAnswer(
                result.ScenarioName,
                result.ModelResponse.Text.Trim(),
                grade.Value ?? "",
                grade.Value is "reliable" or "decline-ok",
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
            text.AppendLine($"Agreement with the strict grade at the library's verdict: {agree}/{scored.Count}"
                + $" (judge passes a strict failure: {scored.Count(a => !a.StrictPassed && !a.Verdicts[name].Failed)},"
                + $" judge fails a strict pass: {scored.Count(a => a.StrictPassed && a.Verdicts[name].Failed)})");

            text.AppendLine("Disagreements:");
            foreach (JudgedAnswer a in scored.Where(a => a.StrictPassed == a.Verdicts[name].Failed))
            {
                string id = a.Scenario.Split('.').Last();
                string routing = id.StartsWith('R') && a.Scenario.StartsWith("Main.", StringComparison.Ordinal) ? " [routing test: strict also passes a decline]" : "";
                text.AppendLine($"  {a.Scenario}: strict {a.StrictGrade}, {name} {a.Verdicts[name].Score} ({(a.Verdicts[name].Failed ? "failed" : "passed")}){routing}");
                text.AppendLine($"    expected: {groundTruths.GetValueOrDefault(id, "?")}");
                text.AppendLine($"    answer:   {a.Answer.ReplaceLineEndings(" ")}");
            }
        }

        return text.ToString();
    }
}
