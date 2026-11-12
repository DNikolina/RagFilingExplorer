using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using RagFilingExplorer.Evaluation.Running;

namespace RagFilingExplorer.Evaluation;

/// <summary>The importer against a store in a temp directory - offline, and nothing written into the repository.</summary>
[TestFixture]
public class HistoricRunImporterTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    private string storage = "";

    [SetUp]
    public void SetUp()
    {
        storage = Path.Combine(Path.GetTempPath(), $"HistoricRunImporterTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(storage, recursive: true);

    // structured-2b's H2 was graded wrong when it ran; "19.4%" became an accepted alternative later, so it grades reliable
    // now and the import stops there - after the run's stored results had already been deleted.
    [Test]
    public async Task ImportAsync_GradeDiffersFromTheRuns_StopsAndKeepsTheStoredRun()
    {
        const string Execution = "2026-10-01-v2-2b-reranked";
        DiskBasedResultStore store = new(storage);
        await store.WriteResultsAsync([new ScenarioRunResult("HeldOut.H2", "1", Execution, DateTime.UtcNow,
            [new ChatMessage(ChatRole.User, "q")], new ChatResponse(new ChatMessage(ChatRole.Assistant, "kept")), new EvaluationResult())]);
        HistoricRun run = new(Execution, DateTime.UtcNow, [("HeldOut", "structured-2b/heldout.json")]);

        Assert.That(async () => await HistoricRunImporter.ImportAsync(Repo, storage, [run]),
            Throws.InvalidOperationException.With.Message.Contains("H2: graded reliable on import, wrong when it ran"));

        List<ScenarioRunResult> kept = await store.ReadResultsAsync(Execution).ToListAsync();
        Assert.That(kept.Select(r => r.ModelResponse.Text), Is.EqualTo(new[] { "kept" }));
    }

    [Test]
    public async Task ImportAsync_GradesAsTheRunGaveThem_ReplacesTheStoredRun()
    {
        HistoricRun run = new("2026-09-28-v1-baseline", DateTime.UtcNow, [("HeldOut", "baseline-v1/heldout.json")]);

        Dictionary<string, int> imported = await HistoricRunImporter.ImportAsync(Repo, storage, [run]);

        Assert.That(imported[run.ExecutionName], Is.EqualTo(15));
        Assert.That(await new DiskBasedResultStore(storage).ReadResultsAsync(run.ExecutionName).CountAsync(), Is.EqualTo(15));
    }
}
