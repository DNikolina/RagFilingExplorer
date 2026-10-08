using Microsoft.Extensions.AI;
using RagFilingExplorer.Claude;

namespace RagFilingExplorer.Evaluation.Judging;

/// <summary>
/// Claude as the judge (evalsettings.json's JudgeModel), for the judges' own requests. Microsoft's judges send
/// Temperature 0 and TopP 1 (Equivalence) or Temperature 0 and MaxOutputTokens 800 (Groundedness) - current Claude models
/// reject a non-default temperature with a 400, and thinking counts toward the output limit - so both are dropped and the
/// limit raised to the Claude ceiling; the judge's effort replaces any the judge set. <see cref="Usage"/> counts the
/// calls that reach the API: below the response cache, a cached verdict adds nothing.
/// </summary>
internal sealed class ClaudeJudgeChatClient(IChatClient inner, ReasoningEffort effort, int maxOutputTokens) : DelegatingChatClient(inner)
{
    /// <summary>The tokens of every judge call billed in this run.</summary>
    public UsageDetails Usage { get; } = new();

    /// <summary>The Claude judge for <paramref name="model"/>: the key and model checked as the Claude app checks them,
    /// claudesettings.json's output ceiling.</summary>
    public static async Task<ClaudeJudgeChatClient> CreateAsync(DirectoryInfo repoRoot, string model, ReasoningEffort effort)
    {
        ClaudeSettings claude = ClaudeSettings.Load(repoRoot);
        claude.Model = model;
        (IChatClient client, _) = await ClaudeChat.CreateAsync(claude);
        return new ClaudeJudgeChatClient(client, effort, claude.MaxOutputTokens);
    }

    /// <summary>The judge's options as sent to Claude: no temperature or top-p, the judge's effort and output ceiling;
    /// everything else (the response format) as the judge set it.</summary>
    internal ChatOptions ForClaude(ChatOptions? options)
    {
        ChatOptions sent = options?.Clone() ?? new ChatOptions();
        sent.Temperature = null;
        sent.TopP = null;
        sent.MaxOutputTokens = maxOutputTokens;
        sent.Reasoning = new ReasoningOptions { Effort = effort };
        return sent;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ChatResponse response = await base.GetResponseAsync(messages, ForClaude(options), cancellationToken);
        if (response.Usage is { } usage)
        {
            Usage.Add(usage);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, ForClaude(options), cancellationToken))
        {
            foreach (UsageContent u in update.Contents.OfType<UsageContent>())
            {
                Usage.Add(u.Details);
            }

            yield return update;
        }
    }
}
