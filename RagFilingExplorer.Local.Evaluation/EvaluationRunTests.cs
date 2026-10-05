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
///   EVAL_SETS       comma-separated sets to run - Main, HeldOut, AnswerSide (default: all three)
///   EVAL_NO_CACHE   1: ask the model afresh and cache nothing - a variance pass (VarianceComparisonTests compares them)
///   EVAL_UNLOAD     1: unload the chat model before every question (with EVAL_NO_CACHE; tools/run-variance.ps1 runs both)
///   EVAL_JUDGE      comma-separated judges - equivalence, groundedness (step 6; answers from the cache, so not with
///                   EVAL_NO_CACHE; JudgeAgreementTests reports the agreement)
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

    /// <summary>The v2 baseline's grade for each question of the set - empty for a set with no v2 baseline (one added
    /// later), so its run is summarized without a comparison instead of failing after every question was asked.</summary>
    internal static Dictionary<string, string> BaselineGrades(string set)
    {
        Dictionary<string, string> grades = new();
        foreach (string path in Baselines.GetValueOrDefault(set, []))
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
        HashSet<string> setNames = (Environment.GetEnvironmentVariable("EVAL_SETS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<QuestionSet> sets = EvaluationRunner.AllSets.Where(s => setNames.Count == 0 || setNames.Contains(s.Name)).ToList();
        Assert.That(sets.Count, Is.EqualTo(setNames.Count == 0 ? EvaluationRunner.AllSets.Count : setNames.Count),
            $"EVAL_SETS names a set that doesn't exist; the sets are {string.Join(", ", EvaluationRunner.AllSets.Select(s => s.Name))}.");
        bool noCache = Environment.GetEnvironmentVariable("EVAL_NO_CACHE") == "1";
        bool unload = Environment.GetEnvironmentVariable("EVAL_UNLOAD") == "1";
        Assert.That(!unload || noCache, "EVAL_UNLOAD without EVAL_NO_CACHE would unload the model for cached answers.");
        List<string> judges = (Environment.GetEnvironmentVariable("EVAL_JUDGE") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(j => j.ToLowerInvariant()).ToList();
        Assert.That(judges.Count == 0 || !noCache, "EVAL_JUDGE judges the cached answers; with EVAL_NO_CACHE it would judge new ones.");

        EvaluationRunner runner = new(Repo, storage, execution, noCache ? null : TimeSpan.FromDays(365), unload, judges);
        List<QuestionOutcome> outcomes = await runner.RunAsync(sets, only, line => TestContext.Progress.WriteLine(line));

        StringBuilder summary = new();
        List<string> differences = new();
        summary.AppendLine($"Execution {execution}: {outcomes.Count} questions, {outcomes.Sum(o => o.Elapsed.TotalMinutes):F0} min"
            + (noCache ? ", every answer asked afresh (no cache)" : "") + (unload ? ", the model unloaded before each" : ""));
        foreach (IGrouping<string, QuestionOutcome> set in outcomes.GroupBy(o => o.Set))
        {
            int passed = set.Count(o => o.Grade.Passed);
            int inContext = set.Count(o => o.Rank is <= 5);
            summary.AppendLine($"{set.Key}: reliable {passed}/{set.Count()}  "
                + string.Join("  ", set.GroupBy(o => o.Grade.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"))
                + $"  | answer in the top 5: {inContext}  | figures: "
                + string.Join("  ", set.GroupBy(o => o.FigureSource).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));

            Dictionary<string, string> baseline = BaselineGrades(set.Key);
            if (baseline.Count == 0)
            {
                summary.AppendLine($"  ({set.Key} has no v2 baseline - its grades aren't compared)");
            }

            foreach (QuestionOutcome o in set)
            {
                if (baseline.TryGetValue(o.Id, out string? before) && before != o.Grade.Status)
                {
                    differences.Add($"{set.Key}.{o.Id}: baseline {before}, now {o.Grade.Status} ({o.Grade.Note}) - {o.Answer}");
                }
            }
        }

        // Step 5: every figure that's in none of the model's excerpts on a question that doesn't ask for a calculation.
        List<QuestionOutcome> untraced = outcomes.Where(o => o.FigureSource == "untraced").ToList();
        summary.AppendLine(untraced.Count == 0 ? "No untraced figure." : $"{untraced.Count} answer(s) state a figure in none of the excerpts:");
        untraced.ForEach(o => summary.AppendLine($"  {o.Set}.{o.Id}: {o.FigureTrace.Replace("\n", " | ")} - {o.Answer}"));
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
