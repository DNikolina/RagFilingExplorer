using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// The project can see the app's internal types (the evaluators wrap them) and build a disk-based
/// reporting configuration - offline, no model, into a temp directory.
/// </summary>
[TestFixture]
public class ProjectSetupTests
{
    private string _tempDirectory = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"ProjectSetupTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    [Test]
    public void AppInternals_AreVisible()
    {
        DirectoryInfo root = RepoPaths.FindRoot(AppContext.BaseDirectory);

        Assert.That(File.Exists(Path.Combine(root.FullName, "tools", "expected-answers.json")), Is.True);
    }

    [Test]
    public void DiskBasedReporting_CreatesAConfigurationWithoutAModel()
    {
        ReportingConfiguration configuration = DiskBasedReportingConfiguration.Create(
            _tempDirectory, evaluators: Array.Empty<IEvaluator>(), enableResponseCaching: false);

        Assert.That(configuration.ExecutionName, Is.EqualTo("Default"));
    }
}
