using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using OllamaSharp;
using OllamaSharp.Models;
using RagFilingExplorer.Local.Evaluation.Evaluators;
using RagFilingExplorer.Local.Evaluation.Grading;

namespace RagFilingExplorer.Local.Evaluation.Judging;

/// <summary>
/// The chat model's client, which raises Ollama's context window while <see cref="JudgeContext"/> is on - only while the
/// judge evaluators run. The Groundedness prompt with five excerpts is ~4.5k tokens, past Ollama's default num_ctx of
/// 4,096, and Ollama drops the oldest tokens silently - here the judge's own instructions (a live constraint: prompt +
/// output must fit num_ctx). The app's answers keep the default: a variance pass must measure the app as it runs.
/// It sits inside the response cache, so the cache keys don't change.
/// </summary>
internal sealed class JudgeContextChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>The judge's context window: the largest judge prompt measured (~6k tokens, Retrieval with its examples)
    /// plus the evaluators' output budget.</summary>
    public const int JudgeContextTokens = 8192;

    public bool JudgeContext { get; set; }

    private ChatOptions? WithContext(ChatOptions? options)
    {
        if (!JudgeContext)
        {
            return options;
        }

        ChatOptions judge = options?.Clone() ?? new ChatOptions();
        judge.AddOllamaOption(OllamaOption.NumCtx, JudgeContextTokens);
        return judge;
    }

    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, WithContext(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, WithContext(options), cancellationToken);
}

/// <summary>
/// v3 step 6, the local-judge spike: Microsoft's Quality evaluators, judged by the app's own chat model, set against the
/// project's deterministic metrics - Equivalence against the strict grade, Groundedness against the figure source.
/// </summary>
internal static class JudgeSetup
{
    public const string Equivalence = "equivalence";
    public const string Groundedness = "groundedness";

    public static readonly IReadOnlyList<string> Known = [Equivalence, Groundedness];

    /// <summary>The evaluators for the judge names given (EVAL_JUDGE).</summary>
    public static List<IEvaluator> Evaluators(IReadOnlyCollection<string> judges)
    {
        List<string> unknown = judges.Where(j => !Known.Contains(j)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException($"Unknown judge(s) {string.Join(", ", unknown)}; known: {string.Join(", ", Known)}.", nameof(judges));
        }

        List<IEvaluator> evaluators = new();
        if (judges.Contains(Equivalence))
        {
            evaluators.Add(new EquivalenceEvaluator());
        }

        if (judges.Contains(Groundedness))
        {
            evaluators.Add(new GroundednessEvaluator());
        }

        return evaluators;
    }

    /// <summary>The contexts the judges read: the ground truth, and the excerpts the model was given, as the prompt shows them.</summary>
    public static List<EvaluationContext> Contexts(ExpectedAnswer expected, IReadOnlyList<RetrievedExcerpt> excerpts, int generationTopK) =>
    [
        new EquivalenceEvaluatorContext(GroundTruth(expected)),
        new GroundednessEvaluatorContext(GroundingText(excerpts.Take(generationTopK))),
    ];

    /// <summary>
    /// The expected answer as text, from tools/expected-answers.json: "$26,445 million", "$3.64", "19%", a fact's strings,
    /// a decline for a negative. A routing test's ground truth is its figure, though the strict grade also passes a decline
    /// there - a disagreement by design, marked in the agreement report.
    /// </summary>
    public static string GroundTruth(ExpectedAnswer expected)
    {
        if (expected.Kind == "negative")
        {
            return "The filings don't contain this information, so the answer should say so instead of giving a figure.";
        }

        IReadOnlyList<string> values = expected.Expect ?? [];
        IEnumerable<string> stated = expected.Unit switch
        {
            "million" or "thousand" => values.Select(v => $"${v} {expected.Unit}"),
            "$" => values.Select(v => $"${v}"),
            "%" => values.Select(v => $"{v}%"),
            _ => values,
        };

        List<string> list = stated.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " and " + list[^1],
        };
    }

    /// <summary>The excerpts as the app's prompt lays them out: a header naming the filing and section, then the chunk.</summary>
    public static string GroundingText(IEnumerable<RetrievedExcerpt> excerpts) =>
        string.Join("\n\n", excerpts.Select(e => $"--- Excerpt from {e.SourceFiling}, section {e.Heading} ---\n{e.Content}"));
}
