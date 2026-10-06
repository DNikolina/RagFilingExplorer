using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation.Running;

/// <summary>One run from before v3, as it's kept in eval/: the graded JSON of each question set it ran, and when.</summary>
internal sealed record HistoricRun(string ExecutionName, DateTime RanAt, IReadOnlyList<(string Set, string Grading)> Gradings);

/// <summary>
/// Brings the v1 and v2 runs kept in eval/ into the evaluation store, so the report shows the project's history next to
/// the v3 runs: each run an execution dated when it ran, each answer a scenario named as the v3 run names it, graded again
/// by <see cref="StrictFigureEvaluator"/> - which must give the grade the run was given, or the import stops. No rank: it
/// needs the chunks the run retrieved, and those indexes were rebuilt as v2 went on - a replay against today's index
/// would print a precise, wrong number.
/// </summary>
internal static class HistoricRunImporter
{
    /// <summary>The main line: v1's baseline and each kept v2 step - not the side experiments. Sets of one configuration
    /// that ran on different days are one execution.</summary>
    public static readonly IReadOnlyList<HistoricRun> MainLine =
    [
        Run("2026-09-28-v1-baseline", "2026-09-28 14:00", ("Main", "baseline-v1/main.json"), ("HeldOut", "baseline-v1/heldout.json")),
        Run("2026-09-28-v2-1c-early", "2026-09-28 15:44", ("Main", "structured-1c-early/main.json"), ("HeldOut", "structured-1c-early/heldout.json")),
        Run("2026-09-28-v2-1b-ii-filing-profile", "2026-09-28 17:28", ("Main", "structured-1b-ii/main.json"), ("HeldOut", "structured-1b-ii/heldout.json")),
        Run("2026-09-29-v2-1b-iii-a-statement-roles", "2026-09-29 10:19", ("Main", "structured-1b-iii-a/main.json"), ("HeldOut", "structured-1b-iii-a/heldout.json")),
        Run("2026-09-29-v2-1b-iii-b-note-topics", "2026-09-29 11:52", ("Main", "structured-1b-iii-b/main.json"), ("HeldOut", "structured-1b-iii-b/heldout.json")),
        Run("2026-09-29-v2-1b-iii-c-period-labels", "2026-09-29 13:02", ("Main", "structured-1b-iii-c/main.json"), ("HeldOut", "structured-1b-iii-c/heldout.json")),
        Run("2026-09-29-v2-1c-a-fiscal-year-names", "2026-09-29 14:14", ("Main", "structured-1c-a/main.json"), ("HeldOut", "structured-1c-a/heldout.json")),
        Run("2026-09-29-v2-1d-company-line", "2026-09-29 20:02", ("Main", "structured-1d/main.json"), ("HeldOut", "structured-1d/heldout.json")),
        Run("2026-09-30-v2-2-hybrid-search", "2026-09-30 10:53", ("Main", "structured-2/main.json"), ("HeldOut", "structured-2-heldout35/heldout.json")),
        Run("2026-09-30-v2-5a-decline-form", "2026-09-30 17:52", ("Main", "structured-5a/main.json"), ("HeldOut", "structured-5a/heldout.json"),
            ("AnswerSide", "answer-side-norerank/answers.json")),
        Run("2026-10-01-v2-2b-reranked", "2026-10-01 19:43", ("Main", "structured-2b/main-merged.json"), ("HeldOut", "structured-2b/heldout.json"),
            ("AnswerSide", "answer-side-baseline/answers.json")),
    ];

    private static HistoricRun Run(string name, string ranAt, params (string Set, string Grading)[] gradings) =>
        new(name, DateTime.SpecifyKind(DateTime.ParseExact(ranAt, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Local).ToUniversalTime(), gradings);

    /// <summary>Writes each run's results into the store at <paramref name="storageRoot"/>; returns answers imported per run.</summary>
    public static async Task<Dictionary<string, int>> ImportAsync(DirectoryInfo repoRoot, string storageRoot, IEnumerable<HistoricRun> runs)
    {
        IReadOnlyDictionary<string, ExpectedAnswer> expected = ExpectedAnswer.LoadAll(repoRoot);
        StrictFigureEvaluator grader = new();
        DiskBasedResultStore store = new(storageRoot);
        Dictionary<string, int> imported = new();

        foreach (HistoricRun run in runs)
        {
            await store.DeleteResultsAsync(run.ExecutionName);
            List<ScenarioRunResult> results = new();
            foreach ((string set, string grading) in run.Gradings)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot.FullName, "eval", grading)));
                foreach (JsonElement r in document.RootElement.GetProperty("results").EnumerateArray())
                {
                    ExpectedAnswer entry = expected[r.GetProperty("id").GetString()!];
                    string answer = r.GetProperty("answer").GetString()!;
                    List<ChatMessage> messages = [new ChatMessage(ChatRole.User, entry.Question)];
                    ChatResponse response = new(new ChatMessage(ChatRole.Assistant, answer));
                    EvaluationResult result = await grader.EvaluateAsync(messages, response, additionalContext: [new ExpectedAnswerContext(entry)]);

                    string stored = (r.TryGetProperty("graded", out JsonElement g) ? g : r.GetProperty("status")).GetString()!;
                    string now = result.Get<StringMetric>(StrictFigureEvaluator.MetricName).Value!;
                    if (now != stored)
                    {
                        throw new InvalidOperationException($"{run.ExecutionName} {entry.Id}: graded {now} on import, {stored} when it ran.");
                    }

                    results.Add(new ScenarioRunResult($"{set}.{entry.Id}", "1", run.ExecutionName, run.RanAt, messages, response, result,
                        tags: ["imported", Path.GetDirectoryName(grading)!]));
                }
            }

            await store.WriteResultsAsync(results);
            imported[run.ExecutionName] = results.Count;
        }

        return imported;
    }
}
