using RagFilingExplorer.Core.Retrieval;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// Voyage's rerank endpoint (POST /v1/rerank) as the app's reranker (Providers:Rerank = Voyage): each question's hybrid
/// candidates scored against it, in place of the local cross-encoder. Every candidate is scored (no top_k), and
/// <c>truncation: false</c> makes an over-long input fail instead of being cut silently - Voyage's default is to cut.
/// The scores come back best first, each with its candidate's index; they're returned in the candidates' order, and a
/// missing or repeated index fails rather than misplacing a score. <see cref="TotalTokens"/> adds up what was billed.
/// It owns its client, disposed with it (the app's runtime disposes its reranker).
/// </summary>
internal sealed class VoyageReranker(HttpClient http, string model, Func<TimeSpan, CancellationToken, Task>? delay = null) : IRelevanceScorer, IDisposable
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay = delay ?? Task.Delay;

    private long totalTokens;

    public string Name => $"Voyage {model}";

    /// <summary>The tokens Voyage counted over every call (its usage.total_tokens) - what the free allowance is spent by.</summary>
    public long TotalTokens => Interlocked.Read(ref totalTokens);

    /// <summary>The reranker after one call scoring a word against itself, so a wrong key or model name fails at startup
    /// rather than on the first question. That check is billed as a few tokens.</summary>
    public static async Task<VoyageReranker> CreateAsync(string? apiKey, string model)
    {
        VoyageReranker reranker = new(VoyageApi.CreateClient(apiKey), model);
        try
        {
            await VoyageApi.CheckAsync(() => reranker.ScoreAsync("revenue", ["revenue"]), $"{VoyageSettings.Section}:RerankModel '{model}'");
            return reranker;
        }
        catch
        {
            reranker.Dispose();
            throw;
        }
    }

    public void Dispose() => http.Dispose();

    public async Task<IReadOnlyList<float>> ScoreAsync(string question, IReadOnlyList<string> passages, CancellationToken cancellationToken = default)
    {
        if (passages.Count == 0)
        {
            return [];
        }

        RerankResponse response = await VoyageApi.PostAsync<RerankResponse>(
            http, "rerank", new RerankRequest(question, passages, model, Truncation: false), delay, cancellationToken);
        Interlocked.Add(ref totalTokens, response.Usage.TotalTokens);

        float?[] scores = new float?[passages.Count];
        foreach (RerankResult result in response.Data)
        {
            if (result.Index < 0 || result.Index >= scores.Length || scores[result.Index] is not null)
            {
                throw new InvalidOperationException($"Voyage's {model} returned index {result.Index} for {passages.Count} candidate(s), out of range or twice.");
            }

            scores[result.Index] = result.RelevanceScore;
        }

        return scores.All(s => s is not null)
            ? scores.Select(s => s!.Value).ToList()
            : throw new InvalidOperationException($"Voyage's {model} scored {response.Data.Count} of {passages.Count} candidate(s).");
    }

    private sealed record RerankRequest(string Query, IReadOnlyList<string> Documents, string Model, bool Truncation);

    private sealed record RerankResponse(List<RerankResult> Data, string Model, RerankUsage Usage);

    private sealed record RerankResult(int Index, float RelevanceScore);

    private sealed record RerankUsage(long TotalTokens);
}
