using RagFilingExplorer.Core;
using RagFilingExplorer.Evaluation.Running;

namespace RagFilingExplorer.Evaluation;

/// <summary>The evaluation's embedder follows the app's settings - offline: refused before any provider is asked.</summary>
[TestFixture]
public class EvaluationIndexTests
{
    private static readonly DirectoryInfo Repo = RepoPaths.FindRoot(AppContext.BaseDirectory);

    // A Voyage model left set while Ollama embeds (or the reverse) must stop the run before Ollama or Voyage is called.
    [TestCase("Ollama", "voyage-4-large")]
    [TestCase("Voyage", "nomic-embed-text")]
    public void OpenAsync_ModelOfAnotherProvider_IsRefusedNamingBothSettings(string provider, string model)
    {
        AppSettings settings = AppSettings.Load(Path.Combine(Repo.FullName, "RagFilingExplorer.Cli"));
        settings.Providers.Embedding = Enum.Parse<EmbeddingProvider>(provider);
        settings.Embedding.Model = model;

        StartupException ex = Assert.ThrowsAsync<StartupException>(() => EvaluationIndex.OpenAsync(settings, Repo))!;

        Assert.That(ex.Message, Does.Contain("Embedding:Model").And.Contain("Providers:Embedding"));
    }
}
