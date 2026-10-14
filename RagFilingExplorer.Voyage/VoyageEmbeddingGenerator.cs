using Microsoft.Extensions.AI;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// Voyage's embeddings endpoint (POST /v1/embeddings) as an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/>, for one
/// model and one input type. The input type is fixed per instance because the vector store calls one generator for
/// everything it embeds: the index is built with a "document" generator and searched with a "query" one
/// (VoyageEmbedding.CreateAsync). Voyage prepends its own retrieval prompt for each type; a swapped or missing type
/// degrades retrieval silently, as a wrong nomic prefix does.
///
/// Sent with <c>truncation: false</c>, so an over-long text fails instead of being cut silently (Voyage's default is to
/// cut). Every vector's size is checked against the model's card (EmbeddingTemplates), so a model that answers with
/// another size fails here, not as a vector-column error inside SQLite. A 429 or 5xx is retried (<see cref="VoyageApi.PostAsync"/>).
/// </summary>
internal sealed class VoyageEmbeddingGenerator(
    HttpClient http, string model, string inputType, int dimensions, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    private readonly EmbeddingGeneratorMetadata metadata = new("voyage", http.BaseAddress, model, dimensions);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<string> inputs = values.ToList();
        EmbeddingsResponse response = await VoyageApi.PostAsync<EmbeddingsResponse>(
            http, "embeddings", new EmbeddingsRequest(inputs, model, inputType, Truncation: false), delay, cancellationToken);

        if (response.Data.Count != inputs.Count)
        {
            throw new InvalidOperationException($"Voyage returned {response.Data.Count} embedding(s) for {inputs.Count} input(s).");
        }

        GeneratedEmbeddings<Embedding<float>> embeddings = new(response.Data.Count)
        {
            Usage = new UsageDetails { InputTokenCount = response.Usage.TotalTokens, TotalTokenCount = response.Usage.TotalTokens },
        };

        foreach (EmbeddingData data in response.Data.OrderBy(d => d.Index))
        {
            if (data.Embedding.Length != dimensions)
            {
                throw new InvalidOperationException(
                    $"Voyage's {model} returned a {data.Embedding.Length}-dimension vector; its card says {dimensions} (EmbeddingTemplates).");
            }

            embeddings.Add(new Embedding<float>(data.Embedding) { ModelId = response.Model });
        }

        return embeddings;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceKey is not null)
        {
            return null;
        }

        if (serviceType == typeof(EmbeddingGeneratorMetadata))
        {
            return metadata;
        }

        return serviceType.IsInstanceOfType(this) ? this : null;
    }

    // The HttpClient is VoyageEmbedding's, shared by the document and query generators.
    public void Dispose()
    {
    }

    private sealed record EmbeddingsRequest(List<string> Input, string Model, string InputType, bool Truncation);

    private sealed record EmbeddingsResponse(List<EmbeddingData> Data, string Model, EmbeddingsUsage Usage);

    private sealed record EmbeddingData(float[] Embedding, int Index);

    private sealed record EmbeddingsUsage(int TotalTokens);
}
