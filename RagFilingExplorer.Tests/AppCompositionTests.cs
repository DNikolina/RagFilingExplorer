using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Retrieval;

namespace RagFilingExplorer.Tests;

[TestFixture]
public class AppCompositionTests
{
    // A provider's reranker as the caller would bring it; never asked to score here.
    private sealed class ProviderScorer : IRelevanceScorer
    {
        public string Name => "provider";

        public Task<IReadOnlyList<float>> ScoreAsync(string question, IReadOnlyList<string> passages, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("not asked in these tests");
    }

    private static AppSettings Reranking(RerankProvider provider)
    {
        AppSettings settings = AppSettingsTests.LoadShippedSettings();
        settings.Retrieval.Rerank = true;
        settings.Providers.Rerank = provider;
        return settings;
    }

    [Test]
    public void ChooseReranker_VoyageWithItsReranker_ReturnsIt()
    {
        ProviderScorer voyage = new();

        Assert.That(AppComposition.ChooseReranker(Reranking(RerankProvider.Voyage), voyage), Is.SameAs(voyage));
    }

    // Core can't create a provider's reranker; a caller that forgot to must fail at startup, not rerank with nothing.
    [Test]
    public void ChooseReranker_VoyageWithoutItsReranker_Throws()
    {
        StartupException ex = Assert.Throws<StartupException>(() => AppComposition.ChooseReranker(Reranking(RerankProvider.Voyage), null))!;

        Assert.That(ex.Message, Does.Contain("Providers:Rerank is Voyage"));
    }

    // Local loads the cross-encoder even when a provider's reranker is passed; a missing model is the proof it tried.
    [Test]
    public void ChooseReranker_LocalWithAProvidersReranker_LoadsTheLocalModel()
    {
        AppSettings settings = Reranking(RerankProvider.Local);
        settings.Retrieval.RerankModelDirectory = Path.Combine(Path.GetTempPath(), $"AppCompositionTests-{Guid.NewGuid():N}");

        StartupException ex = Assert.Throws<StartupException>(() => AppComposition.ChooseReranker(settings, new ProviderScorer()))!;

        Assert.That(ex.Message, Does.Contain("model.onnx").And.Contain("Fetch the model"));
    }
}
