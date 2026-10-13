using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// another size fails here, not as a vector-column error inside SQLite.
/// </summary>
internal sealed class VoyageEmbeddingGenerator(
    HttpClient http, string model, string inputType, int dimensions, Func<TimeSpan, CancellationToken, Task>? delay = null)
    : IEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>Attempts per request: a 429 (rate limit) or a 5xx is retried with exponential back-off, as Voyage's error
    /// codes page advises; any other failure is not.</summary>
    internal const int MaxAttempts = 8;

    private static readonly TimeSpan MaxBackOff = TimeSpan.FromSeconds(60);

    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    private readonly EmbeddingGeneratorMetadata metadata = new("voyage", http.BaseAddress, model, dimensions);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        List<string> inputs = values.ToList();
        EmbeddingsResponse response = await PostAsync(new EmbeddingsRequest(inputs, model, inputType, Truncation: false), cancellationToken);

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

    private async Task<EmbeddingsResponse> PostAsync(EmbeddingsRequest request, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync("embeddings", request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken)
                    ?? throw new InvalidOperationException("Voyage returned an empty response.");
            }

            bool retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retryable || attempt == MaxAttempts)
            {
                // Voyage's 4xx body is {"detail": "..."}; a 5xx may have none.
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                string detail = TryReadDetail(body) ?? body;
                throw new HttpRequestException(
                    $"Voyage answered {(int)response.StatusCode} {response.StatusCode}{(retryable ? $" after {MaxAttempts} attempts" : "")}: {detail}",
                    inner: null, response.StatusCode);
            }

            await delay(BackOff(attempt), cancellationToken);
        }
    }

    /// <summary>1, 2, 4 ... seconds, at most <see cref="MaxBackOff"/> - about two minutes over every attempt.</summary>
    internal static TimeSpan BackOff(int attempt) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), MaxBackOff.TotalSeconds));

    private static string? TryReadDetail(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out JsonElement detail) ? detail.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record EmbeddingsRequest(
        [property: JsonPropertyName("input")] List<string> Input,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input_type")] string InputType,
        [property: JsonPropertyName("truncation")] bool Truncation);

    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("data")] List<EmbeddingData> Data,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("usage")] EmbeddingsUsage Usage);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("embedding")] float[] Embedding,
        [property: JsonPropertyName("index")] int Index);

    private sealed record EmbeddingsUsage([property: JsonPropertyName("total_tokens")] int TotalTokens);
}
