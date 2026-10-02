using System.Globalization;
using System.Text;
using System.Text.Json;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// v3 step 4: a full evaluation run of the app as configured - every question asked, graded and ranked, stored under
/// eval/v3-runs/ (one scenario per question; responses cached there, gitignored), with report-&lt;execution&gt;.html and
/// summary-&lt;execution&gt;.txt written at the end. Each grade is compared with the v2 baselines' (structured-5a main and
/// held-out, answer-side-norerank - the grader's own verdict, as GraderParityTests reads them); a difference is a
/// warning, listed in the summary. [Explicit]: Ollama, the
/// built index, and about two hours of CPU. Environment variables:
///   EVAL_EXECUTION  the execution name (default: structured-hybrid-&lt;yyyyMMddTHHmm&gt;)
///   EVAL_ONLY       comma-separated question ids to run (default: all 102) - a few minutes' smoke run first
///   dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~EvaluationRunTests"
/// </summary>
[TestFixture]
[Explicit("Needs Ollama, the built index and about two hours.")]
public class EvaluationRunTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    // The v2 baselines each set is held to (the configuration the defaults ship: Structured, Hybrid, no reranking).
    private static readonly Dictionary<string, string[]> Baselines = new()
    {
        ["Main"] = ["eval/structured-5a/main.json"],
        ["HeldOut"] = ["eval/structured-5a/heldout.json"],
        ["AnswerSide"] = ["eval/answer-side-norerank/answers.json"],
    };

    private static Dictionary<string, string> BaselineGrades(string set)
    {
        Dictionary<string, string> grades = new();
        foreach (string path in Baselines[set])
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repo.FullName, path)));
            foreach (JsonElement r in document.RootElement.GetProperty("results").EnumerateArray())
            {
                grades[r.GetProperty("id").GetString()!] = (r.TryGetProperty("graded", out JsonElement g) ? g : r.GetProperty("status")).GetString()!;
            }
        }

        return grades;
    }

    [Test]
    public async Task Run_DefaultConfiguration_ReproducesTheV2BaselineGrades()
    {
        string execution = Environment.GetEnvironmentVariable("EVAL_EXECUTION") is { Length: > 0 } name
            ? name
            : $"structured-hybrid-{DateTime.Now.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture)}";
        HashSet<string> only = (Environment.GetEnvironmentVariable("EVAL_ONLY") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");

        // A year: long enough to regrade a run without re-asking the model; the library's own default is 14 days. A
        // package upgrade can still change the cache keys (Microsoft's docs), which means fresh responses, not wrong ones.
        EvaluationRunner runner = new(Repo, storage, execution, TimeSpan.FromDays(365));
        List<QuestionOutcome> outcomes = await runner.RunAsync(EvaluationRunner.AllSets, only, line => TestContext.Progress.WriteLine(line));

        StringBuilder summary = new();
        List<string> differences = new();
        summary.AppendLine($"Execution {execution}: {outcomes.Count} questions, {outcomes.Sum(o => o.Elapsed.TotalMinutes):F0} min");
        foreach (IGrouping<string, QuestionOutcome> set in outcomes.GroupBy(o => o.Set))
        {
            int passed = set.Count(o => o.Grade.Passed);
            int inContext = set.Count(o => o.Rank is <= 5);
            summary.AppendLine($"{set.Key}: reliable {passed}/{set.Count()}  "
                + string.Join("  ", set.GroupBy(o => o.Grade.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
                + $"  | answer in the top 5: {inContext}");

            Dictionary<string, string> baseline = BaselineGrades(set.Key);
            foreach (QuestionOutcome o in set)
            {
                if (baseline.TryGetValue(o.Id, out string? before) && before != o.Grade.Status)
                {
                    differences.Add($"{set.Key}.{o.Id}: baseline {before}, now {o.Grade.Status} ({o.Grade.Note}) - {o.Answer}");
                }
            }
        }

        summary.AppendLine(differences.Count == 0 ? "Every grade equals the v2 baseline." : $"{differences.Count} grade(s) differ from the v2 baseline:");
        differences.ForEach(d => summary.AppendLine("  " + d));
        File.WriteAllText(Path.Combine(storage, $"summary-{execution}.txt"), summary.ToString());
        TestContext.Progress.WriteLine(summary.ToString());

        // A warning, not a failure (user, 2026-10-02): llama3.1:8b at temperature 0 doesn't repeat exactly here - the first
        // full run's A16 added an unrequested "$620 million" to the same prompt - so a difference is something to read,
        // not proof the evaluation broke. The evaluators themselves are held exactly by GraderParityTests and
        // RetrievalParityTests.
        if (differences.Count > 0)
        {
            Assert.Warn($"{differences.Count} grade(s) differ from the v2 baseline:\n" + string.Join("\n", differences));
        }
    }
}
