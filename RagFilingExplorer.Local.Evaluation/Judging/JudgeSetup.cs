using System.Globalization;
using System.Text.RegularExpressions;
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
    /// <summary>The judge's context window: Groundedness's ~4.5k-token prompt plus the evaluators' output budget, with
    /// room for the largest Quality prompt measured (Retrieval's ~6k - measured, not offered as a judge).</summary>
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
/// v3 step 6, the local judge - kept alongside the strict grade, never the grade (Graders: judge or both): Microsoft's
/// Quality evaluators, judged by the app's own chat model, set against the project's deterministic metrics - Equivalence
/// against the strict grade, Groundedness against the figure source.
/// </summary>
internal static partial class JudgeSetup
{
    public const string Equivalence = "equivalence";
    public const string Groundedness = "groundedness";

    public static readonly IReadOnlyList<string> Known = [Equivalence, Groundedness];

    /// <summary>The evaluators for the judge names given (evalsettings.json's Judges, when Graders includes them).</summary>
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
            evaluators.Add(new ScoreOnlyEquivalenceEvaluator());
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

    // The score as llama3.1:8b writes it instead of the bare integer asked for: "Therefore, the Equivalence score is 4.",
    // "I would rate the Equivalence metric as 4 stars." (the smoke test), and - the most common in the first full run -
    // "the Equivalence metric should be 5", "the Equivalence metric value is 5", "the Equivalence score should be 5". A
    // score word (score, metric, value, rating, rate) must come first: a reply cut off before its score (the evaluator caps
    // its length) can end on a figure ("$4 million"), which isn't a score. The last such phrase wins.
    [GeneratedRegex(@"\b(?:score|metric|value|rating|rate[ds]?)\b[^.\d]{0,30}?\b(?:is|of|be|as|at)\s*(?:an?\s+)?\**([1-5])\b(?![.,]\d)"
        + @"|\b(?:score|metric|value|rating)\s*[:=]\s*\**([1-5])\b(?![.,]\d)|\b([1-5])\s*(?:/\s*5|out of 5|stars?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WrittenScoreRegex();

    [GeneratedRegex(@"^\**([1-5])\**(?![\d.,%])\s", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingScoreRegex();

    private const string UnreadReplyMarker = "from the following text:";

    /// <summary>
    /// The score a judge wrote in words, recovered from the library's "Failed to parse numeric score" error, which quotes
    /// the reply; null when the error isn't that or no score phrase is found. A second reading next to the library's -
    /// the agreement report counts the two apart (user, 2026-10-05).
    /// </summary>
    public static int? RecoverScore(string? error)
    {
        int at = error?.IndexOf(UnreadReplyMarker, StringComparison.Ordinal) ?? -1;
        if (at < 0)
        {
            return null;
        }

        return ScoreInReply(error![(at + UnreadReplyMarker.Length)..]);
    }

    /// <summary>The score in a judge's reply written in words ("...the Equivalence metric should be 5."), or null.</summary>
    public static int? ScoreInReply(string reply)
    {
        reply = reply.Trim();
        Match? last = WrittenScoreRegex().Matches(reply).LastOrDefault();
        if (last is not null)
        {
            return int.Parse(last.Groups.Values.Skip(1).First(g => g.Success).Value, CultureInfo.InvariantCulture);
        }

        // The score first, then an explanation the library didn't expect: "4  The predicted answer is mostly similar..." (Q4).
        Match leading = LeadingScoreRegex().Match(reply);
        return leading.Success ? int.Parse(leading.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A chat client that replies with one fixed text - the judge's reply, written as the library asks for it.</summary>
    private sealed class FixedReplyChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Whether the library interprets <paramref name="score"/> as failed - asked of the library itself: the evaluator is
    /// run against a client replying with exactly that score in the format its prompt asks for, so a recovered score gets
    /// the same verdict a readable reply would, whatever the library's threshold.
    /// </summary>
    public static async Task<bool> LibraryFailsAsync(string metricName, int score)
    {
        IEvaluator evaluator;
        string reply;
        EvaluationContext context;
        if (metricName == EquivalenceEvaluator.EquivalenceMetricName)
        {
            (evaluator, reply, context) = (new EquivalenceEvaluator(), score.ToString(CultureInfo.InvariantCulture), new EquivalenceEvaluatorContext("g"));
        }
        else if (metricName == GroundednessEvaluator.GroundednessMetricName)
        {
            (evaluator, reply, context) = (new GroundednessEvaluator(), $"<S0>-</S0><S1>-</S1><S2>{score}</S2>", new GroundednessEvaluatorContext("c"));
        }
        else
        {
            throw new ArgumentException($"No judge has the metric {metricName}.", nameof(metricName));
        }

        EvaluationResult result = await evaluator.EvaluateAsync(
            [new ChatMessage(ChatRole.User, "q")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "a")),
            new ChatConfiguration(new FixedReplyChatClient(reply)),
            [context]);
        NumericMetric metric = result.Get<NumericMetric>(metricName);
        if (metric.Value != score)
        {
            throw new InvalidOperationException($"The library read {metricName} {score} as {metric.Value?.ToString() ?? "no score"}.");
        }

        return metric.Interpretation?.Failed ?? false;
    }

    /// <summary>The excerpts as the app's prompt lays them out: a header naming the filing and section, then the chunk.</summary>
    public static string GroundingText(IEnumerable<RetrievedExcerpt> excerpts) =>
        string.Join("\n\n", excerpts.Select(e => $"--- Excerpt from {e.SourceFiling}, section {e.Heading} ---\n{e.Content}"));
}
