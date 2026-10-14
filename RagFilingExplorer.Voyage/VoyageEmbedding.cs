using Microsoft.Extensions.AI;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// Voyage as the embedder, when appsettings.json's Providers:Embedding is Voyage: the model is Embedding:Model, and nothing
/// else is configured - the endpoint is fixed and the key is never a setting (<see cref="VoyageApi.ReadKey"/>).
/// </summary>
internal static class VoyageEmbedding
{
    /// <summary>
    /// The document generator (builds the index) and the query generator (searches it), sharing one client - after one
    /// query embedding of a single word, so a wrong key or model fails at startup rather than after chunking. That check
    /// is billed as a few tokens.
    /// </summary>
    public static async Task<(IEmbeddingGenerator<string, Embedding<float>> Documents, IEmbeddingGenerator<string, Embedding<float>> Queries)> CreateAsync(
        string? apiKey, EmbeddingTemplates model)
    {
        HttpClient http = VoyageApi.CreateClient(apiKey);
        VoyageEmbeddingGenerator queries = new(http, model.Model, "query", model.Dimensions);
        await VoyageApi.CheckAsync(() => queries.GenerateAsync(["revenue"]), $"Embedding:Model '{model.Model}'");
        return (new VoyageEmbeddingGenerator(http, model.Model, "document", model.Dimensions), queries);
    }
}
