using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Tests.Voyage;

[TestFixture]
public class VoyageApiTests
{
    private string directory = "";

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"VoyageApiTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(directory, recursive: true);

    // A settings file with the given JSON, then the given keys as user secrets or environment variables would add them.
    private IConfiguration Configuration(string settingsJson, Dictionary<string, string?>? later = null)
    {
        File.WriteAllText(Path.Combine(directory, "appsettings.json"), settingsJson);
        return new ConfigurationBuilder()
            .SetBasePath(directory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddInMemoryCollection(later ?? [])
            .Build();
    }

    // The repository is public: a key in appsettings.json would be committed with it.
    [TestCase("""{ "Voyage": { "ApiKey": "pa-secret" } }""", "Voyage:ApiKey")]
    [TestCase("""{ "VOYAGE_API_KEY": "pa-secret" }""", "VOYAGE_API_KEY")]
    public void ReadKey_KeyInTheSettingsFile_IsRefused(string settingsJson, string keyName)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => VoyageApi.ReadKey(Configuration(settingsJson)));

        Assert.That(ex.Message, Does.Contain(keyName).And.Not.Contain("pa-secret"));
    }

    [Test]
    public void ReadKey_EnvironmentAndSecrets_PrefersTheEnvironment()
    {
        string? key = VoyageApi.ReadKey(Configuration("{}", new() { ["Voyage:ApiKey"] = "pa-secrets", ["VOYAGE_API_KEY"] = "pa-environment" }));

        Assert.That(key, Is.EqualTo("pa-environment"));
    }

    [Test]
    public void ReadKey_NoKey_IsNull()
    {
        Assert.That(VoyageApi.ReadKey(Configuration("{}")), Is.Null);
    }

    // Both fail before any request is sent.
    [Test]
    public void CreateClient_NoKey_ThrowsNamingWhereItGoes()
    {
        StartupException ex = Assert.Throws<StartupException>(() => VoyageApi.CreateClient(null))!;

        Assert.That(ex.Message, Does.Contain("Voyage:ApiKey").And.Contain("VOYAGE_API_KEY"));
    }

    [Test]
    public void CreateClient_KeyWithAControlCharacter_Throws()
    {
        StartupException ex = Assert.Throws<StartupException>(() => VoyageApi.CreateClient("\u0016"))!;

        Assert.That(ex.Message, Does.Contain("non-printable"));
    }

    [Test]
    public void BackOff_DoublesUpToAMinute()
    {
        Assert.That(Enumerable.Range(1, 8).Select(a => VoyageApi.BackOff(a).TotalSeconds), Is.EqualTo(new double[] { 1, 2, 4, 8, 16, 32, 60, 60 }));
    }
}
