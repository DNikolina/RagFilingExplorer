namespace RagFilingExplorer.Core.Retrieval;

/// <summary>
/// Scores how well each passage answers a question - higher is better; only the order means anything. A local model
/// (CrossEncoderReranker) or a provider's API (Providers:Rerank), so the call is asynchronous.
/// </summary>
internal interface IRelevanceScorer
{
    string Name { get; }

    /// <summary>One score per passage, in the passages' order.</summary>
    Task<IReadOnlyList<float>> ScoreAsync(string question, IReadOnlyList<string> passages, CancellationToken cancellationToken = default);
}
