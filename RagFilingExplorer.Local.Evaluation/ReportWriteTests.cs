using RagFilingExplorer.Local.Evaluation.Running;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// Rewrites eval/v3-runs/report.html from every execution stored there - after a change to how the report is written,
/// without asking the model again. Offline, but [Explicit], because it writes into the repository:
///   dotnet test RagFilingExplorer.Local.Evaluation --filter "FullyQualifiedName~ReportWriteTests"
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

        Assert.That(new FileInfo(report).LastWriteTimeUtc, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
    }
}
