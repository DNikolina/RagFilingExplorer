using Microsoft.Extensions.AI;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Retrieval;
using RagFilingExplorer.Core.VectorStore;
using RagFilingExplorer.Ollama;
using RagFilingExplorer.Voyage;

namespace RagFilingExplorer.Evaluation.Running;

/// <summary>
/// The app's built index, opened for an evaluation with the app's embedder: appsettings.json's Providers:Embedding and
/// Embedding:Model, as the console chooses them - an environment variable overrides either for one run
/// (<c>Providers__Embedding=Voyage</c>, <c>Embedding__Model=voyage-4-large</c>). Only questions are embedded here, so
/// Voyage's is its "query" generator. The reranker too is the app's (Retrieval:Rerank, Providers:Rerank, and the Voyage
/// section's RerankModel - <c>Voyage__RerankModel=rerank-3-lite</c> for one run). The chat model is the evaluation's own
/// choice (evalsettings.json's ChatModel).
/// </summary>
internal static class EvaluationIndex
{
    public static async Task<RagRuntime> OpenAsync(
        AppSettings settings, DirectoryInfo repoRoot, IChatClient? chatClient = null, ChatModelOptions? chatModel = null)
    {
        EmbeddingTemplates embedding = EmbeddingTemplates.For(settings.Embedding.Model);
        embedding.EnsureServedBy(settings.Providers.Embedding);

        IEmbeddingGenerator<string, Embedding<float>>? queries = settings.Providers.Embedding == EmbeddingProvider.Voyage
            ? (await VoyageEmbedding.CreateAsync(VoyageApi.LoadKeyFromRepository(repoRoot), embedding)).Queries
            : null;
        VoyageReranker? reranker = settings.Retrieval.Rerank && settings.Providers.Rerank == RerankProvider.Voyage
            ? await VoyageReranker.CreateAsync(VoyageApi.LoadKeyFromRepository(repoRoot), VoyageSettings.LoadFromRepository(repoRoot).RerankModel)
            : null;
        return await OllamaSetup.OpenExistingIndexAsync(settings, repoRoot, chatClient, chatModel, queries, reranker);
    }
}
