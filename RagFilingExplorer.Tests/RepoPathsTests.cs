namespace RagFilingExplorer.Tests;

[TestFixture]
public class RepoPathsTests
{
    // The test binary lives under RagFilingExplorer.Tests/bin/..., like the app's own build output,
    // so this exercises the same walk-up the app does regardless of the working directory.
    [Test]
    public void FindRoot_FromBuildOutput_FindsTheRepoRoot()
    {
        DirectoryInfo root = RepoPaths.FindRoot(AppContext.BaseDirectory);

        Assert.That(File.Exists(Path.Combine(root.FullName, "RagFilingExplorer.slnx")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(root.FullName, "data")), Is.True);
    }

    [Test]
    public void FindRoot_OutsideTheRepo_ThrowsStartupException()
    {
        string outside = Path.Combine(Path.GetTempPath(), $"RepoPathsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        try
        {
            Assert.Throws<StartupException>(() => RepoPaths.FindRoot(outside));
        }
        finally
        {
            Directory.Delete(outside);
        }
    }
}
