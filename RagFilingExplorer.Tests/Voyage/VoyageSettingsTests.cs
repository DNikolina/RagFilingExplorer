using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Tests.Voyage;

[TestFixture]
public class VoyageSettingsTests
{
    private static string ShippedSettingsDirectory =>
        Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "RagFilingExplorer.Cli");

    // The shipped file, with any keys overridden as an environment variable would (a null value removes the key).
    private static VoyageSettings FromShipped(Dictionary<string, string?>? overrides = null) => VoyageSettings.From(
        new ConfigurationBuilder()
            .SetBasePath(ShippedSettingsDirectory)
            .AddJsonFile(VoyageSettings.FileName, optional: false)
            .AddInMemoryCollection(overrides ?? [])
            .Build());

    [Test]
    public void From_ShippedSettings_RerankModelIsRerank3()
    {
        Assert.That(FromShipped().RerankModel, Is.EqualTo("rerank-3"));
    }

    [Test]
    public void From_Override_ReplacesTheModelForOneRun()
    {
        Assert.That(FromShipped(new() { ["Voyage:RerankModel"] = "rerank-3-lite" }).RerankModel, Is.EqualTo("rerank-3-lite"));
    }

    [Test]
    public void From_MissingKey_ThrowsNamingIt()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => FromShipped(new() { ["Voyage:RerankModel"] = null }))!;

        Assert.That(ex.Message, Does.Contain("Voyage:RerankModel"));
    }
}
