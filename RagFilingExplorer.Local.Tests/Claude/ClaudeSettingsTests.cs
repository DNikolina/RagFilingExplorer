using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Claude;

namespace RagFilingExplorer.Local.Tests.Claude;

[TestFixture]
public class ClaudeSettingsTests
{
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
    public void From_EnvironmentStyleOverride_ReplacesTheShippedValue()
    {
        ClaudeSettings settings = FromShipped(new() { ["Claude:Model"] = "claude-sonnet-5-5" });

        Assert.That(settings.Model, Is.EqualTo("claude-sonnet-5-5"));
    }
}
