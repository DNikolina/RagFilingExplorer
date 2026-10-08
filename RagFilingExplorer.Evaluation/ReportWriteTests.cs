using RagFilingExplorer.Evaluation.Running;

namespace RagFilingExplorer.Evaluation;

/// <summary>
/// Rewrites eval/v3-runs/report.html from every execution stored there, and each run's report-<execution>.html - after a
/// change to how the report is written or to stored results,
/// without asking the model again. Offline, but [Explicit], because it writes into the repository:
///   dotnet test RagFilingExplorer.Evaluation --filter "FullyQualifiedName~ReportWriteTests"
/// </summary>
[TestFixture]
[Explicit("Writes into eval/v3-runs/.")]
public class ReportWriteTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    [Test]
    public async Task WriteReportAsync_EveryExecution_WritesTheHistoryReport()
    {
        string storage = Path.Combine(Repo.FullName, "eval", "v3-runs");
        string report = Path.Combine(storage, "report.html");

        await EvaluationRunner.WriteReportAsync(storage, report, executionName: null);

        // Each run's own report-<execution>.html, where there is one (runs made by EvaluationRunTests).
        foreach (string runReport in Directory.GetFiles(storage, "report-*.html"))
        {
            string execution = Path.GetFileNameWithoutExtension(runReport)["report-".Length..];
            await EvaluationRunner.WriteReportAsync(storage, runReport, execution);
        }

        Assert.That(new FileInfo(report).LastWriteTimeUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
    }
}
