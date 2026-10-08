using Microsoft.Extensions.AI;
using RagFilingExplorer.Evaluation.Judging;

namespace RagFilingExplorer.Evaluation;

/// <summary>The Claude judge's requests, offline - what reaches the API in place of what Microsoft's judges send.</summary>
[TestFixture]
public class ClaudeJudgeChatClientTests
{
    private sealed class Recorder : IChatClient
    {
        public ChatOptions? Sent { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "5"))
            {
                Usage = new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 },
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent = options;
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "5");
            yield return new ChatResponseUpdate { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 900, OutputTokenCount = 40 })] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    // Equivalence's own options (dotnet/extensions 10.10.0): Temperature 0 and TopP 1 - a 400 from current Claude models.
    [Test]
    public async Task GetResponseAsync_EquivalenceOptions_SentWithoutTemperatureAndWithTheJudgesEffort()
    {
        Recorder inner = new();
        ClaudeJudgeChatClient judge = new(inner, ReasoningEffort.Low, 16000);
        ChatOptions equivalence = new() { Temperature = 0.0f, TopP = 1.0f, ResponseFormat = ChatResponseFormat.Text };

        await judge.GetResponseAsync([new ChatMessage(ChatRole.User, "q")], equivalence);

        Assert.That(inner.Sent!.Temperature, Is.Null);
        Assert.That(inner.Sent.TopP, Is.Null);
        Assert.That(inner.Sent.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.Low));
        Assert.That(inner.Sent.ResponseFormat, Is.SameAs(ChatResponseFormat.Text), "the judge's response format is kept");
        Assert.That(equivalence.Temperature, Is.EqualTo(0.0f), "the judge's own options are not changed");
    }

    // Groundedness asks for at most 800 output tokens - thinking counts toward the limit, so it's raised to the ceiling.
    [Test]
    public async Task GetResponseAsync_GroundednessOutputLimit_RaisedToTheCeiling()
    {
        Recorder inner = new();

        await new ClaudeJudgeChatClient(inner, ReasoningEffort.Low, 16000).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "q")], new ChatOptions { Temperature = 0.0f, MaxOutputTokens = 800 });

        Assert.That(inner.Sent!.MaxOutputTokens, Is.EqualTo(16000));
    }

    [Test]
    public async Task GetStreamingResponseAsync_EquivalenceOptions_SentForClaudeAndUsageCounted()
    {
        Recorder inner = new();
        ClaudeJudgeChatClient judge = new(inner, ReasoningEffort.Low, 16000);

        List<ChatResponseUpdate> updates = [];
        await foreach (ChatResponseUpdate update in judge.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "q")], new ChatOptions { Temperature = 0.0f, TopP = 1.0f }))
        {
            updates.Add(update);
        }

        Assert.That(updates.ToChatResponse().Text, Is.EqualTo("5"), "every update passed through");
        Assert.That(inner.Sent!.Temperature, Is.Null);
        Assert.That(inner.Sent.TopP, Is.Null);
        Assert.That(inner.Sent.Reasoning?.Effort, Is.EqualTo(ReasoningEffort.Low));
        Assert.That(judge.Usage.InputTokenCount, Is.EqualTo(900));
        Assert.That(judge.Usage.OutputTokenCount, Is.EqualTo(40));
    }

    [Test]
    public async Task Usage_EveryCallThatReachesTheApi_Added()
    {
        ClaudeJudgeChatClient judge = new(new Recorder(), ReasoningEffort.Low, 16000);

        await judge.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);
        await judge.GetResponseAsync([new ChatMessage(ChatRole.User, "q")]);

        Assert.That(judge.Usage.InputTokenCount, Is.EqualTo(1800));
        Assert.That(judge.Usage.OutputTokenCount, Is.EqualTo(80));
    }
}
