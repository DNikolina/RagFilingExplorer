using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using RagFilingExplorer.Local.Evaluation.Judging;

namespace RagFilingExplorer.Local.Evaluation;

/// <summary>Microsoft's Equivalence evaluator given the judge's score alone, offline - a stub client stands in for the judge.</summary>
[TestFixture]
public class ScoreOnlyEquivalenceEvaluatorTests
{
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

    private static async Task<NumericMetric> JudgeAsync(string reply)
    {
        EvaluationResult result = await new ScoreOnlyEquivalenceEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, "How much cash did Microsoft pay in common stock dividends in fiscal 2026?")],
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Microsoft paid $27,034 million.")),
            new ChatConfiguration(new FixedReplyChatClient(reply)),
            [new EquivalenceEvaluatorContext("$26,445 million")]);
        return result.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName);
    }

    [Test]
    public async Task EvaluateAsync_ScoreInASentence_ParsedByTheLibraryAsItsOwn()
    {
        // A10's reply in the first full run, word for word: read as given, the library failed to parse it.
        const string reply = "The predicted answer is mostly similar to the correct answer, but with a slight difference in the amount. Therefore, the Equivalence score is 4.";

        NumericMetric metric = await JudgeAsync(reply);

        Assert.That(metric.Value, Is.EqualTo(4));
        Assert.That(metric.Interpretation!.Failed, Is.False, "the library's own rule: below 4 fails");
        Assert.That(metric.Diagnostics, Is.Null.Or.Empty);
        Assert.That(metric.Metadata![ScoreOnlyEquivalenceEvaluator.JudgeReplyKey], Is.EqualTo(reply));
        Assert.That(metric.Metadata![ScoreOnlyEquivalenceEvaluator.TakenFromWordsKey], Is.EqualTo("yes"));
    }

    [Test]
    public async Task EvaluateAsync_BareScore_Unchanged()
    {
        NumericMetric metric = await JudgeAsync("2");

        Assert.That(metric.Value, Is.EqualTo(2));
        Assert.That(metric.Interpretation!.Failed, Is.True);
        Assert.That(metric.Metadata![ScoreOnlyEquivalenceEvaluator.TakenFromWordsKey], Is.EqualTo("no"));
    }

    [Test]
    public async Task EvaluateAsync_NoScoreInTheReply_StillFailsToParse()
    {
        // T7: the judge repeated the answer and gave no score.
        NumericMetric metric = await JudgeAsync("The predicted answer is: The effect of the Ireland statutory tax rate difference was a decrease of 2.6%.");

        Assert.That(metric.Value, Is.Null);
        Assert.That(metric.Diagnostics!.Single().Severity, Is.EqualTo(EvaluationDiagnosticSeverity.Error));
        Assert.That(metric.Metadata![ScoreOnlyEquivalenceEvaluator.TakenFromWordsKey], Is.EqualTo("no"));
    }
}
