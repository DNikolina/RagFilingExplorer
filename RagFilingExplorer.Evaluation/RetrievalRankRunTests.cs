using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using OllamaSharp;
using RagFilingExplorer.Evaluation.Evaluators;
using RagFilingExplorer.Evaluation.Running;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Evaluation;

/// <summary>
/// Every question's answer rank, retrieval only: the app's own search (AppComposition, as the evaluation asks it), the
/// answer stream never read and the chat model never asked (<see cref="RetrievalParityTests.NoChatClient"/>) - so an
/// embedder or retrieval change is measured in minutes, not llama's two hours. The rank is the evaluation's
/// (<see cref="RetrievalRankEvaluator.RankOf"/>) among Retrieval:VerboseSearchTopK chunks. Writes
/// eval/v3-runs/ranks-&lt;embedding model&gt;.txt: recall@1, recall@5 and MRR per set over the answerable questions, and
/// each rank beside <see cref="Baseline"/>'s stored one, every move listed. The embedding model is appsettings.json's, or
/// Ollama__EmbeddingModel for one run; its index must be built. Needs Ollama. [Explicit]:
///   Ollama__EmbeddingModel=embeddinggemma dotnet test RagFilingExplorer.Evaluation --filter "FullyQualifiedName~RetrievalRankRunTests"
/// </summary>
[TestFixture]
[Explicit("Needs Ollama and the built index.")]
public class RetrievalRankRunTests
{
    // nomic-embed-text's ranks, stored with v3's answers; retrieval on later Ollama builds reproduced them exactly
    // (docs/Decision-Log.md, "Batch 3 - embeddings, free then paid", step 1).
    private const string Baseline = "structured-hybrid-v3-baseline";

    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    [Test]
    public async Task Rank_EveryQuestion_WritesRanksBesideTheBaseline()
    {
        AppSettings settings = AppSettings.Load(Path.Combine(Repo.FullName, "RagFilingExplorer.Cli"));
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");
        Dictionary<string, int?> baseline = (await EvaluationRunner.LatestResultsAsync(storage, Baseline))
            .ToDictionary(r => r.ScenarioName, r => Rank(r.EvaluationResult));

        using RagRuntime runtime = await OllamaSetup.OpenExistingIndexAsync(settings, Repo, new RetrievalParityTests.NoChatClient());
        (OllamaApiClient embedding, _) = OllamaSetup.CreateClients(settings.Ollama);
        string ollamaVersion = (await embedding.GetVersionAsync()).ToString();

        List<(string Set, string Scenario, int? Rank, int? Before)> ranks = [];
        foreach ((QuestionSet set, string question, var entry) in EvaluationRunner.SelectQuestions(Repo, EvaluationRunner.AllSets, new HashSet<string>()))
        {
            // A negative has nothing to find, so no rank (as the evaluation's rank evaluator).
            if (entry.ChunkExpect is not { Count: > 0 } expected)
            {
                continue;
            }

            RagAnswer answer = await runtime.AnswerService.AskAsync(question, settings.Retrieval.VerboseSearchTopK);
            string scenario = $"{set.Name}.{entry.Id}";
            ranks.Add((set.Name, scenario, RetrievalRankEvaluator.RankOf(answer.RetrievedChunks.Select(r => r.Record.Content).ToList(), expected),
                baseline.GetValueOrDefault(scenario)));
        }

        string index = IndexFiles.For(Repo, ChunkingStrategies.FileName(settings.Chunking.Strategy), EmbeddingTemplates.For(settings.Ollama.EmbeddingModel)).DbFileName;
        StringBuilder report = new();
        report.AppendLine($"Ranks: {settings.Ollama.EmbeddingModel} ({index}), Ollama {ollamaVersion}, {ranks.Count} answerable questions, "
            + $"ranked among {settings.Retrieval.VerboseSearchTopK}; beside {Baseline} (nomic-embed-text). A rank > {settings.Retrieval.VerboseSearchTopK} is '-'.");
        foreach (IGrouping<string, (string Set, string Scenario, int? Rank, int? Before)> set in ranks.GroupBy(r => r.Set).Append(ranks.GroupBy(_ => "All").Single()))
        {
            report.AppendLine($"{set.Key}: {Metrics(set.Select(r => r.Rank).ToList())}  | baseline: {Metrics(set.Select(r => r.Before).ToList())}");
        }

        List<(string Set, string Scenario, int? Rank, int? Before)> moved = ranks.Where(r => r.Rank != r.Before).ToList();
        report.AppendLine($"Moved ({moved.Count}), better first:");
        foreach ((_, string scenario, int? rank, int? before) in moved.OrderBy(r => Reciprocal(r.Before) - Reciprocal(r.Rank)))
        {
            report.AppendLine($"  {scenario}: {Show(before)} -> {Show(rank)}");
        }

        report.AppendLine("Every rank (this run, baseline):");
        foreach ((_, string scenario, int? rank, int? before) in ranks)
        {
            report.AppendLine($"  {scenario}: {Show(rank)}, {Show(before)}");
        }

        string path = Path.Combine(storage, $"ranks-{settings.Ollama.EmbeddingModel.Replace(':', '-')}.txt");
        await File.WriteAllTextAsync(path, report.ToString());
        TestContext.Progress.WriteLine(report.ToString());
    }

    private static int? Rank(EvaluationResult result) =>
        result.Metrics.TryGetValue(RetrievalRankEvaluator.MetricName, out EvaluationMetric? metric) && metric is NumericMetric { Value: { } value }
            ? (int)value
            : null;

    private static double Reciprocal(int? rank) => rank is { } r ? 1.0 / r : 0;

    private static string Show(int? rank) => rank?.ToString(CultureInfo.InvariantCulture) ?? "-";

    private static string Metrics(IReadOnlyList<int?> ranks) =>
        $"recall@1 {ranks.Count(r => r == 1)}/{ranks.Count}  recall@5 {ranks.Count(r => r <= 5)}/{ranks.Count}  "
        + $"MRR {ranks.Average(Reciprocal).ToString("F3", CultureInfo.InvariantCulture)}";
}
