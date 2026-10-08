using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Claude;

namespace RagFilingExplorer.Tests.Claude;

[TestFixture]
public class ClaudeChatTests
{
    private static ClaudeSettings WithKey(string? key) => ClaudeSettings.From(new ConfigurationBuilder()
        .SetBasePath(Path.Combine(RepoPaths.FindRoot(AppContext.BaseDirectory).FullName, "RagFilingExplorer.Claude"))
        .AddJsonFile(ClaudeSettings.FileName)
        .AddInMemoryCollection([new KeyValuePair<string, string?>("Claude:ApiKey", key)])
        .Build());

    // Ctrl+V at PowerShell's hidden Read-Host prompt stored the control character itself as the key (one character),
    // and the API answered the model check with a 400 with no body.
    [TestCase("\u0016")]
    [TestCase("sk-ant-abc ")]
    public void CreateAsync_KeyWithUnprintableCharacter_FailsBeforeAnyCall(string key)
    {
        StartupException ex = Assert.ThrowsAsync<StartupException>(() => ClaudeChat.CreateAsync(WithKey(key)))!;

        Assert.That(ex.Message, Does.Contain("failed paste"));
    }

    [Test]
    public void CreateAsync_NoKey_NamesWhereToSetIt()
    {
        StartupException ex = Assert.ThrowsAsync<StartupException>(() => ClaudeChat.CreateAsync(WithKey(null)))!;

        Assert.That(ex.Message, Does.Contain("dotnet user-secrets set Claude:ApiKey").And.Contain("ANTHROPIC_API_KEY"));
    }
}
