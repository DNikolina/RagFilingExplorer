using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Claude;

namespace RagFilingExplorer.Tests.Claude;

[TestFixture]
public class ClaudeSettingsTests
{
    private string directory = "";

    [SetUp]
    public void SetUp()
    {
        directory = Path.Combine(Path.GetTempPath(), $"ClaudeSettingsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string ShippedSettingsDirectory =>
        Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "RagFilingExplorer.Claude");

    // The shipped file, with any keys overridden as an environment variable would (a null value removes the key).
    private static ClaudeSettings FromShipped(Dictionary<string, string?>? overrides = null) => ClaudeSettings.From(
        new ConfigurationBuilder()
            .SetBasePath(ShippedSettingsDirectory)
            .AddJsonFile(ClaudeSettings.FileName, optional: false)
            .AddInMemoryCollection(overrides ?? [])
            .Build());

    [Test]
    public void From_ShippedSettings_BindsEveryValue()
    {
        ClaudeSettings settings = FromShipped();

        Assert.That(settings.Model, Is.EqualTo("claude-opus-5-5"));
        Assert.That(settings.LookupEffort, Is.EqualTo(ReasoningEffort.Medium), "enum binds from its name");
        Assert.That(settings.SynthesisEffort, Is.EqualTo(ReasoningEffort.Medium));
        Assert.That(settings.MaxOutputTokens, Is.Positive);
    }

    // The SDK sends ReasoningEffort.None as thinking disabled, which current Opus and Sonnet models reject with a 400 -
    // on every question, so it's refused at startup instead.
    [TestCase("Claude:LookupEffort")]
    [TestCase("Claude:SynthesisEffort")]
    public void From_EffortNone_Throws(string key)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => FromShipped(new() { [key] = "None" }));

        Assert.That(ex.Message, Does.Contain("can't be None"));
    }

    [Test]
    public void From_MissingKey_ThrowsNamingThatKey()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => FromShipped(new() { ["Claude:Model"] = null }));

        Assert.That(ex.Message, Does.Contain("Claude:Model"));
    }

    [Test]
    public void From_ShippedSettings_HoldNoKey()
    {
        Assert.That(FromShipped().ApiKey, Is.Null);
    }

    [Test]
    public void From_UserSecretsKey_IsTheKey()
    {
        Assert.That(FromShipped(new() { ["Claude:ApiKey"] = "from-secrets" }).ApiKey, Is.EqualTo("from-secrets"));
    }

    [Test]
    public void From_EnvironmentKeyAndUserSecretsKey_EnvironmentWins()
    {
        ClaudeSettings settings = FromShipped(new() { ["Claude:ApiKey"] = "from-secrets", ["ANTHROPIC_API_KEY"] = "from-environment" });

        Assert.That(settings.ApiKey, Is.EqualTo("from-environment"));
    }

    // User secrets are loaded as a JSON file too (secrets.json); the settings-file guard must not refuse the key there.
    [Test]
    public void From_KeyInAnotherJsonFile_IsTheKey()
    {
        File.WriteAllText(Path.Combine(directory, "secrets.json"), "{\"Claude:ApiKey\": \"from-secrets\"}");
        IConfiguration configuration = new ConfigurationBuilder()
            .SetBasePath(ShippedSettingsDirectory)
            .AddJsonFile(ClaudeSettings.FileName)
            .AddJsonFile(Path.Combine(directory, "secrets.json"))
            .Build();

        Assert.That(ClaudeSettings.From(configuration).ApiKey, Is.EqualTo("from-secrets"));
    }

    // The repository is public: a key written into the settings file would be committed with it - under either name the
    // key is read by.
    [TestCase("\"Model\":", "\"ApiKey\": \"sk-ant-x\", \"Model\":", "Claude:ApiKey")]
    [TestCase("\"Claude\": {", "\"ANTHROPIC_API_KEY\": \"sk-ant-x\", \"Claude\": {", "ANTHROPIC_API_KEY")]
    public void From_KeyInTheSettingsFile_Throws(string at, string withKey, string keyName)
    {
        string shipped = File.ReadAllText(Path.Combine(ShippedSettingsDirectory, ClaudeSettings.FileName));
        File.WriteAllText(Path.Combine(directory, ClaudeSettings.FileName), shipped.Replace(at, withKey));
        IConfiguration configuration = new ConfigurationBuilder().SetBasePath(directory).AddJsonFile(ClaudeSettings.FileName).Build();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => ClaudeSettings.From(configuration));

        Assert.That(ex.Message, Does.Contain($"holds {keyName}").And.Contain("the repository is public"));
    }

    [Test]
    public void From_EnvironmentStyleOverride_ReplacesTheShippedValue()
    {
        ClaudeSettings settings = FromShipped(new() { ["Claude:Model"] = "claude-sonnet-5-5" });

        Assert.That(settings.Model, Is.EqualTo("claude-sonnet-5-5"));
    }
}
