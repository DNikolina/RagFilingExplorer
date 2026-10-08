using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using RagFilingExplorer.Evaluation.Evaluators;
using RagFilingExplorer.Evaluation.Grading;

namespace RagFilingExplorer.Evaluation;

/// <summary>The rank rule and its evaluator, offline - the comparison with the replay is RetrievalParityTests' (needs Ollama).</summary>
[TestFixture]
public class RetrievalRankEvaluatorTests
{
    private static readonly string[] Chunks =
    [
        "Retained earnings > Common stock cash dividends — 2026: (27,034)",
        "Financing > Common stock cash dividends paid — 2026: (26,445)",
        "Net income — 2026: $17,087 | Total revenues — 2026: 67,357",
    ];

    [TestCase(new[] { "(26,445)" }, 2)]
    [TestCase(new[] { "17,087", "67,357" }, 3)]   // an arithmetic question ranks at its last input
    [TestCase(new[] { "(27,034)", "(26,445)" }, 2)]
    [TestCase(new[] { "(99,999)" }, null)]
    public void RankOf_FirstChunkHoldingEachString_TheLastOfThem(string[] expected, int? rank)
    {
        Assert.That(RetrievalRankEvaluator.RankOf(Chunks, expected), Is.EqualTo(rank));
    }

    private static async Task<NumericMetric> RankAsync(ExpectedAnswer expected, int generationTopK = 5)
    {
        EvaluationResult result = await new RetrievalRankEvaluator(generationTopK).EvaluateAsync(
            [new ChatMessage(ChatRole.User, expected.Question)],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "")),
            additionalContext: [new ExpectedAnswerContext(expected), new RetrievedChunksContext(Chunks.Select(c => new RetrievedExcerpt("MSFT-10K-2026.html", "h", "narrative", c)).ToList())]);
        return result.Get<NumericMetric>(RetrievalRankEvaluator.MetricName);
    }

    [Test]
    public async Task EvaluateAsync_AnswerInTheModelsContext_IsGood()
    {
        NumericMetric metric = await RankAsync(new("A10", "q", "figure", ChunkExpect: ["(26,445)"]));

        Assert.That(metric.Value, Is.EqualTo(2));
        Assert.That(metric.Interpretation!.Failed, Is.False);
    }

    [Test]
    public async Task EvaluateAsync_AnswerBeyondGenerationTopK_Fails()
    {
        NumericMetric metric = await RankAsync(new("A10", "q", "figure", ChunkExpect: ["(26,445)"]), generationTopK: 1);

        Assert.That(metric.Value, Is.EqualTo(2));
        Assert.That(metric.Interpretation!.Failed, Is.True);
    }

    [Test]
    public async Task EvaluateAsync_Negative_HasNoRankAndDoesNotFail()
    {
        NumericMetric metric = await RankAsync(new("A12", "q", "negative", ChunkExpect: []));

        Assert.That(metric.Value, Is.Null);
        Assert.That(metric.Interpretation!.Failed, Is.False);
    }
}
