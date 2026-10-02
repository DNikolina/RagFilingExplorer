using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// Imports the v1 and v2 main-line runs from eval/ into eval/v3-runs/ (HistoricRunImporter - re-graded, each grade checked
/// against the one the run was given) and writes eval/v3-runs/report.html from every execution there. Offline - no
/// Ollama - but [Explicit], because it writes into the repository:
///   dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~HistoricRunImportTests"
/// </summary>
[TestFixture]
[Explicit("Writes into eval/v3-runs/.")]
public class HistoricRunImportTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    [Test]
    public async Task ImportAsync_MainLine_EveryAnswerImportedWithItsGrade()
    {
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");

        Dictionary<string, int> imported = await HistoricRunImporter.ImportAsync(Repo, storage, HistoricRunImporter.MainLine);
        await EvaluationRunner.WriteReportAsync(storage, Path.Combine(storage, "report.html"), executionName: null);

        // 40 main questions in every run; H1-H15 until step 2 added H16-H35; A1-A27 from step 5a.
        Assert.That(imported.Values.Take(8), Is.All.EqualTo(55));
        Assert.That(imported["2026-09-30-v2-2-hybrid-search"], Is.EqualTo(75));
        Assert.That(imported["2026-09-30-v2-5a-decline-form"], Is.EqualTo(102));
        Assert.That(imported["2026-10-01-v2-2b-reranked"], Is.EqualTo(102));
        TestContext.Progress.WriteLine(string.Join("\n", imported.Select(kv => $"{kv.Key}: {kv.Value}")));
    }
}
