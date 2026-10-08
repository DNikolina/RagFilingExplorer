using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using OllamaSharp;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>
/// The response cache keyed by the answering model, offline. A cached answer replays when its key matches, and the key
/// holds no model unless the library adds the reporting configuration's chat client provider and model id - without
/// them, a Claude run would replay llama's cached answers, and a Sonnet run Opus's, as its own.
/// </summary>
[TestFixture]
public class ResponseCacheKeyTests
{
    private string storage = "";

    [SetUp]
    public void SetUp() => storage = Path.Combine(Path.GetTempPath(), $"ResponseCacheKeyTests-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(storage))
        {
            Directory.Delete(storage, recursive: true);
        }
    }

    private IReadOnlyList<string> CachingKeys(IChatClient answering) =>
        DiskBasedReportingConfiguration.Create(storage, [], new ChatConfiguration(answering), executionName: "keys").CachingKeys;

    [Test]
    public void CachingKeys_Claude_HoldTheProviderAndModel()
    {
        using AnthropicClient client = new() { ApiKey = "offline" };

        Assert.That(CachingKeys(client.AsIChatClient("claude-sonnet-5-5", 16000)), Is.EqualTo(new[] { "anthropic", "claude-sonnet-5-5" }));
    }

    [Test]
    public void CachingKeys_Ollama_HoldTheProviderAndModel()
    {
        using OllamaApiClient client = new(new Uri("http://localhost:11434"), "llama3.1:8b");

        Assert.That(CachingKeys(client), Does.Contain("llama3.1:8b"));
    }
}
