using Microsoft.Extensions.AI;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Local.Tests;

[TestFixture]
public class AppSettingsTests
{
    private string _tempDirectory = null!;

    internal static string ShippedSettingsDirectory =>
        Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "RagFilingExplorer.Local");

    internal static AppSettings LoadShippedSettings() => AppSettings.Load(ShippedSettingsDirectory);

    [SetUp]
    public void SetUp()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"AppSettingsTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDirectory, recursive: true);

    private static string ShippedJson() => File.ReadAllText(Path.Combine(ShippedSettingsDirectory, "appsettings.json"));

    private void WriteTempSettings(string json) => File.WriteAllText(Path.Combine(_tempDirectory, "appsettings.json"), json);

    [Test]
    public void Load_ShippedAppSettings_BindsEveryValue()
    {
        AppSettings settings = LoadShippedSettings();

        Assert.That(settings.Ollama.ChatModel, Is.Not.Empty);
        Assert.That(settings.Retrieval.ReasoningEffort, Is.EqualTo(ReasoningEffort.Medium), "enum binds from its name");
        Assert.That(settings.Chunking.Strategy, Is.EqualTo(ChunkingStrategyKind.Structured), "enum binds from its name");
        Assert.That(settings.Retrieval.Search, Is.EqualTo(SearchMode.Hybrid), "enum binds from its name");
        Assert.That(settings.Retrieval.Rerank, Is.False, "reranking is opt-in");
    }

    // MaxOutputTokens shipped as 4096 - Ollama's whole default context window, which the prompt shares, so
    // the ceiling could never be reached: runaway llama3.1:8b answers (1,400-1,700 tokens, in Ollama's
    // server log) filled the window first, and Ollama then dropped the oldest tokens - the system prompt
    // and the top-ranked chunks. The largest prompt measured is ~3,100 tokens.
    [Test]
    public void Load_ShippedAppSettings_OutputCeilingLeavesRoomForThePromptInOllamasDefaultContext()
    {
        const int OllamaDefaultContextTokens = 4096;
        const int LargestMeasuredPromptTokens = 3103;

        AppSettings settings = LoadShippedSettings();

        Assert.That(settings.Retrieval.MaxOutputTokens + LargestMeasuredPromptTokens, Is.LessThanOrEqualTo(OllamaDefaultContextTokens));
    }

    [Test]
    public void RequiredConfigurationKeys_CoversEveryLeafSetting_AndNoSections()
    {
        string[] keys = AppSettings.RequiredConfigurationKeys().ToArray();

        Assert.That(keys, Has.Length.EqualTo(21));
        Assert.That(keys, Does.Contain("Ollama:ChatModel"));
        Assert.That(keys, Does.Contain("Retrieval:ReasoningEffort"));
        Assert.That(keys, Does.Contain("Retrieval:Search"));
        Assert.That(keys, Does.Contain("Retrieval:RerankModelSha256"));
        Assert.That(keys, Does.Contain("Chunking:Strategy"));
        Assert.That(keys, Does.Not.Contain("Ollama"), "a section is not a leaf key");
    }

    // The whole point of the presence check: ConfigurationBinder ignores `required`, so without it a
    // missing key binds as null/0 and the app starts anyway.
    [Test]
    public void Load_MissingKey_ThrowsNamingThatKey()
    {
        WriteTempSettings(ShippedJson().Replace("\"ChatModel\": \"llama3.1:8b\"", "\"UnrelatedKey\": \"x\""));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => AppSettings.Load(_tempDirectory));

        Assert.That(ex.Message, Does.Contain("Ollama:ChatModel"));
    }

    [Test]
    public void Load_ZeroValuedKeys_AreNotMistakenForMissing()
    {
        // ChatTemperature already ships as 0, so the shipped value itself is the zero case for it.
        WriteTempSettings(ShippedJson().Replace("\"OverlapTokens\": 50", "\"OverlapTokens\": 0"));

        AppSettings settings = AppSettings.Load(_tempDirectory);

        Assert.That(settings.Chunking.OverlapTokens, Is.Zero);
        Assert.That(settings.Retrieval.ChatTemperature, Is.Zero);
    }

    [Test]
    public void Load_MisspelledReasoningEffort_FailsAtLoadTime()
    {
        WriteTempSettings(ShippedJson().Replace("\"ReasoningEffort\": \"Medium\"", "\"ReasoningEffort\": \"Medum\""));

        Assert.Throws<InvalidOperationException>(() => AppSettings.Load(_tempDirectory));
    }

    [Test]
    public void Load_MisspelledChunkingStrategy_FailsAtLoadTime()
    {
        // The misspelling replaces the shipped value, so it must be the shipped value - when the default moved from
        // Markdown to Structured (v2), the replace silently matched nothing and the test failed on valid settings.
        const string Shipped = "\"Strategy\": \"Structured\"";
        Assert.That(ShippedJson(), Does.Contain(Shipped), "the shipped strategy this test misspells");
        WriteTempSettings(ShippedJson().Replace(Shipped, "\"Strategy\": \"Structred\""));

        Assert.Throws<InvalidOperationException>(() => AppSettings.Load(_tempDirectory));
    }
}
