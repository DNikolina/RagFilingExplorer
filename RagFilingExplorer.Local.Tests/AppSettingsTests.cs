using Microsoft.Extensions.AI;

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
    }

    [Test]
    public void RequiredConfigurationKeys_CoversEveryLeafSetting_AndNoSections()
    {
        string[] keys = AppSettings.RequiredConfigurationKeys().ToArray();

        Assert.That(keys, Has.Length.EqualTo(14));
        Assert.That(keys, Does.Contain("Ollama:ChatModel"));
        Assert.That(keys, Does.Contain("Retrieval:ReasoningEffort"));
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
        WriteTempSettings(ShippedJson()
            .Replace("\"OverlapTokens\": 50", "\"OverlapTokens\": 0")
            .Replace("\"ChatTemperature\": 0.2", "\"ChatTemperature\": 0"));

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
}
