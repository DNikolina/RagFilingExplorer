using System.Globalization;
using System.Text;
using System.Text.Json;
using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// A full evaluation run of the app as configured - every question asked, graded and ranked, stored under
/// eval/v3-runs/ (one scenario per question; responses cached there, gitignored), with report-&lt;execution&gt;.html and
/// summary-&lt;execution&gt;.txt written at the end. Each strict grade is compared with the v2 baselines' (structured-5a main
/// and held-out, answer-side-norerank - the grader's own verdict, as GraderParityTests reads them); a difference is a
/// warning, listed in the summary. [Explicit]: Ollama, the built index, and about two hours of CPU.
///
/// Set up by evalsettings.json (<see cref="EvaluationSettings"/>): the graders - strict, judge or both - the execution name,
/// the sets and questions, fresh answers or cached; an environment variable overrides a key for one run with the same name
/// (Evaluation__Execution=..., Evaluation__Only=Q1,A16 for a smoke run, Evaluation__Graders=both):
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

    /// <summary>The v2 baseline's grade for each question of the set - empty for a set with no v2 baseline, so its run is
    /// summarized without a comparison instead of failing after every question was asked.</summary>
    internal static Dictionary<string, string> BaselineGrades(string set)
    {
        Dictionary<string, string> grades = [];
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
        EvaluationSettings settings = EvaluationSettings.Load(Repo);
        string execution = settings.Execution is { Length: > 0 } name
            ? name
            : $"structured-hybrid-{DateTime.Now.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture)}";
        List<QuestionSet> sets = EvaluationRunner.AllSets
            .Where(s => settings.SetNames.Count == 0 || settings.SetNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");

        EvaluationRunner runner = new(Repo, storage, execution,
            settings.NoCache ? null : TimeSpan.FromDays(settings.CacheTimeToLiveDays),
            settings.UnloadEachQuestion, settings.JudgeNames, settings.UsesStrictGrade, settings.ChatModel);
        List<QuestionOutcome> outcomes = await runner.RunAsync(sets, settings.OnlyIds.ToHashSet(), line => TestContext.Progress.WriteLine(line));

        // What the model reads - Retrieval:GenerationTopK, from the app's appsettings.json.
        int generationTopK = AppSettings.Load(Path.Combine(Repo.FullName, "RagFilingExplorer.Local")).Retrieval.GenerationTopK;
        StringBuilder summary = new();
        List<string> differences = [];
        summary.AppendLine($"Execution {execution}: {outcomes.Count} questions, {outcomes.Sum(o => o.Elapsed.TotalMinutes):F0} min, chat model {settings.ChatModel}, graders {settings.Graders}"
            + (settings.JudgeNames.Count > 0 ? $" ({string.Join(", ", settings.JudgeNames)})" : "")
            + (settings.NoCache ? ", every answer asked afresh (no cache)" : "") + (settings.UnloadEachQuestion ? ", the model unloaded before each" : ""));
        foreach (IGrouping<string, QuestionOutcome> set in outcomes.GroupBy(o => o.Set))
        {
            int inContext = set.Count(o => o.Rank <= generationTopK);
            string strict = settings.UsesStrictGrade
                ? $"reliable {set.Count(o => o.Grade!.Passed)}/{set.Count()}  "
                    + string.Join("  ", set.GroupBy(o => o.Grade!.Status).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")) + "  | "
                : "";
            string judged = string.Concat(set.SelectMany(o => o.JudgePassed ?? new Dictionary<string, bool>()).GroupBy(kv => kv.Key)
                .Select(g => $"{g.Key} passes {g.Count(kv => kv.Value)}/{g.Count()}  | "));
            summary.AppendLine($"{set.Key}: {strict}{judged}answer in the top {generationTopK}: {inContext}  | figures: "
                + string.Join("  ", set.GroupBy(o => o.FigureSource).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}")));

            if (!settings.UsesStrictGrade)
            {
                continue;
            }

            Dictionary<string, string> baseline = BaselineGrades(set.Key);
            if (baseline.Count == 0)
            {
                summary.AppendLine($"  ({set.Key} has no v2 baseline - its grades aren't compared)");
            }

            foreach (QuestionOutcome o in set)
            {
                if (baseline.TryGetValue(o.Id, out string? before) && before != o.Grade!.Status)
                {
                    differences.Add($"{set.Key}.{o.Id}: baseline {before}, now {o.Grade.Status} ({o.Grade.Note}) - {o.Answer}");
                }
            }
        }

        // A Claude run's cost: thinking counts as output. A cached answer's tokens are those it took when first asked.
        summary.AppendLine($"Tokens: {outcomes.Sum(o => o.Usage?.InputTokenCount ?? 0):N0} input, {outcomes.Sum(o => o.Usage?.OutputTokenCount ?? 0):N0} output.");

        // Every figure that's in none of the model's excerpts on a question that doesn't ask for a calculation.
        List<QuestionOutcome> untraced = outcomes.Where(o => o.FigureSource == "untraced").ToList();
        summary.AppendLine(untraced.Count == 0 ? "No untraced figure." : $"{untraced.Count} answer(s) state a figure in none of the excerpts:");
        untraced.ForEach(o => summary.AppendLine($"  {o.Set}.{o.Id}: {o.FigureTrace.Replace("\n", " | ")} - {o.Answer}"));
        if (settings.UsesStrictGrade)
        {
            summary.AppendLine(differences.Count == 0 ? "Every grade equals the v2 baseline." : $"{differences.Count} grade(s) differ from the v2 baseline:");
            differences.ForEach(d => summary.AppendLine("  " + d));
        }
        else
        {
            summary.AppendLine("Not strictly graded (Graders: judge) - no comparison with the v2 baseline.");
        }

        if (runner.KeptFromEarlierRun.Count > 0)
        {
            summary.AppendLine($"{runner.KeptFromEarlierRun.Count} result(s) from an earlier run under this name weren't asked again and stay part of it"
                + $" (choose a new Execution name to keep runs apart): {string.Join(", ", runner.KeptFromEarlierRun)}");
        }

        File.WriteAllText(Path.Combine(storage, $"summary-{execution}.txt"), summary.ToString());
        TestContext.Progress.WriteLine(summary.ToString());

        // A warning, not a failure: a difference is something to read, not proof the evaluation broke - within one Ollama
        // build the model repeats exactly, but an Ollama update can change answers.
        // The evaluators themselves are held exactly by GraderParityTests and RetrievalParityTests.
        if (differences.Count > 0)
        {
            Assert.Warn($"{differences.Count} grade(s) differ from the v2 baseline:\n" + string.Join("\n", differences));
        }

        if (runner.KeptFromEarlierRun.Count > 0)
        {
            Assert.Warn($"{runner.KeptFromEarlierRun.Count} result(s) from an earlier run named {execution} are mixed into this one.");
        }
    }
}
