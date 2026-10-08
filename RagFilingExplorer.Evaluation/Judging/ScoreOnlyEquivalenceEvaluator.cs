using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace RagFilingExplorer.Evaluation.Judging;

/// <summary>
/// The judge's chat client, seen by the evaluator: a reply that writes its score in words ("...Therefore, the Equivalence
/// metric should be 5.") reaches the evaluator as the score alone ("5"); a bare score, or a reply with no score in it,
/// goes through as it is. Wraps the scenario's (caching) client, so a cached reply is treated the same as a new one.
/// </summary>
internal sealed class ScoreOnlyChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>The judge's last reply as it wrote it.</summary>
    public string? LastReply { get; private set; }

    /// <summary>Whether the last reply's score was taken from its words.</summary>
    public bool ScoreTakenFromWords { get; private set; }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, options, cancellationToken);
        string reply = response.Text.Trim();
        LastReply = reply;
        ScoreTakenFromWords = false;
        if (int.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) || JudgeSetup.ScoreInReply(reply) is not int score)
        {
            return response;
        }

        ScoreTakenFromWords = true;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, score.ToString(CultureInfo.InvariantCulture)))
        {
            ModelId = response.ModelId,
            Usage = response.Usage,
            ResponseId = response.ResponseId,
            CreatedAt = response.CreatedAt,
            FinishReason = response.FinishReason,
            AdditionalProperties = response.AdditionalProperties,
        };
    }
}

/// <summary>
/// Microsoft's <see cref="EquivalenceEvaluator"/>, given the judge's score alone. Its parser requires the
/// whole reply to be the number (<c>TryParseEvaluationResponseWithValue</c>: the trimmed text, parsed as a value) - its
/// prompt asks for "a single integer value ... no other text", and it was tested only with GPT-4o. llama3.1:8b writes the
/// score in a sentence, so read as given most replies fail to parse. The score is taken from the words as
/// <see cref="JudgeSetup.ScoreInReply"/> reads them - in the spirit of the library's own repair prompt for malformed JSON
/// replies - and the library then parses and interprets it as its own (scores below 4 fail). Nothing is hidden: the metric
/// keeps the judge's reply ("Judge reply") and says whether its score was taken from the words; a reply with no score in it
/// still fails to parse, as the library reports it.
/// </summary>
internal sealed class ScoreOnlyEquivalenceEvaluator : IEvaluator
{
    public const string JudgeReplyKey = "Judge reply";
    public const string TakenFromWordsKey = "Score taken from the reply's words";

    private readonly EquivalenceEvaluator inner = new();

    public IReadOnlyCollection<string> EvaluationMetricNames => inner.EvaluationMetricNames;

    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatConfiguration);
        ScoreOnlyChatClient client = new(chatConfiguration.ChatClient);
        EvaluationResult result = await inner.EvaluateAsync(messages, modelResponse, new ChatConfiguration(client), additionalContext, cancellationToken);

        NumericMetric metric = result.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName);
        if (client.LastReply is { } reply)
        {
            metric.AddOrUpdateMetadata(JudgeReplyKey, reply);
            metric.AddOrUpdateMetadata(TakenFromWordsKey, client.ScoreTakenFromWords ? "yes" : "no");
        }

        return result;
    }
}
