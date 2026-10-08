using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Evaluation.Grading;

namespace RagFilingExplorer.Evaluation.Evaluators;

/// <summary>One retrieved chunk: what the prompt's excerpt header names (filing, section), its statement type, and its text.</summary>
internal sealed record RetrievedExcerpt(string SourceFiling, string Heading, string StatementType, string Content);

/// <summary>The chunks the app retrieved for the question, best first (RagAnswer.RetrievedChunks), as additional context.
/// The stored context is each chunk's text only; the metadata travels in <see cref="Excerpts"/>.</summary>
internal sealed class RetrievedChunksContext(IReadOnlyList<RetrievedExcerpt> excerpts)
    : EvaluationContext(ContextName, excerpts.Select(e => (AIContent)new TextContent(e.Content)).ToList())
{
    public const string ContextName = "Retrieved chunks";

    public IReadOnlyList<RetrievedExcerpt> Excerpts { get; } = excerpts;

    public IReadOnlyList<string> Chunks { get; } = excerpts.Select(e => e.Content).ToList();
}

/// <summary>
/// Where the answer ranks among the chunks the app retrieved - tools/replay_recall.py's rank_of(), read from the app's own
/// results instead of a replayed log: the 1-based position of the first chunk holding each of the question's
/// chunk_expect strings, the last of them for a question with several (an arithmetic question's inputs); null if one is
/// in none of the chunks. A rank within GenerationTopK is in what the model reads. A negative has nothing
/// to find, so no rank. Deterministic - no model is asked. <paramref name="generationTopK"/> is Retrieval:GenerationTopK,
/// from appsettings.json - not repeated here.
/// </summary>
internal sealed class RetrievalRankEvaluator(int generationTopK) : IEvaluator
{
    public const string MetricName = "Answer rank";

    public IReadOnlyCollection<string> EvaluationMetricNames => [MetricName];

    /// <summary>rank_of() - the last expected string's first position (1-based), or null if any is missing.</summary>
    public static int? RankOf(IReadOnlyList<string> chunks, IReadOnlyList<string> expected)
    {
        int last = 0;
        foreach (string e in expected)
        {
            int position = 0;
            for (int i = 0; i < chunks.Count && position == 0; i++)
            {
                if (chunks[i].Contains(e, StringComparison.Ordinal))
                {
                    position = i + 1;
                }
            }

            if (position == 0)
            {
                return null;
            }

            last = Math.Max(last, position);
        }

        return last;
    }

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        List<EvaluationContext> contexts = additionalContext?.ToList() ?? [];
        ExpectedAnswer expected = EvaluationContexts.Require<ExpectedAnswerContext>(contexts, nameof(RetrievalRankEvaluator)).Expected;
        IReadOnlyList<string> chunks = EvaluationContexts.Require<RetrievedChunksContext>(contexts, nameof(RetrievalRankEvaluator)).Chunks;

        // The sentence is the interpretation's reason - the report's "Why this score?"; the metric's own reason, shown as
        // "What this measures?", is the description.
        string description =
            "Where the expected figure first appears among the chunks retrieved for the question (1 = the top). Ranks 1-"
            + $"{generationTopK} are in the model's prompt; a later rank, or none, means retrieval missed it - so a wrong answer "
            + "with a good rank is the model's misreading. Deterministic - ported from tools/replay_recall.py and matched to its "
            + "ranks on all 102 questions (RetrievalParityTests).";
        NumericMetric metric;
        if (expected.ChunkExpect is not { Count: > 0 } chunkExpect)
        {
            metric = new NumericMetric(MetricName, value: null, description)
            {
                Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, reason: "Not scored: a negative has no answer to find."),
            };
        }
        else
        {
            int? rank = RankOf(chunks, chunkExpect);
            string where = rank is null
                ? $"{string.Join(", ", chunkExpect)} not in the {chunks.Count} retrieved chunks"
                : $"{string.Join(", ", chunkExpect)} at rank {rank} of {chunks.Count}";
            metric = new NumericMetric(MetricName, rank, description)
            {
                Interpretation = rank is not null && rank <= generationTopK
                    ? new EvaluationMetricInterpretation(rank == 1 ? EvaluationRating.Exceptional : EvaluationRating.Good, reason: $"{where} - in the model's context (top {generationTopK}).")
                    : new EvaluationMetricInterpretation(EvaluationRating.Unacceptable, failed: true, reason: $"{where} - outside the model's context (top {generationTopK})."),
            };
        }

        return new ValueTask<EvaluationResult>(new EvaluationResult(metric));
    }
}
