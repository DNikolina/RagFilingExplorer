using Microsoft.Extensions.AI;

namespace RagFilingExplorer.Core.Retrieval;

/// <summary>
/// How <see cref="RagAnswerService"/> calls its chat model, besides the client: the temperature (null sends none, for a
/// model that rejects one), the reasoning effort for a single-fact lookup and for a question that
/// <see cref="QueryIntentResolver.RequiresSynthesis"/> flags, the output ceiling, and the context window prompt and output
/// share when it's small enough to check (null for Claude's 1M - see PromptFit). The composition builds it from the model's
/// settings and capabilities, so the service never asks a provider what its model can do.
/// </summary>
internal sealed record ChatModelOptions(
    float? Temperature, ReasoningEffort LookupEffort, ReasoningEffort SynthesisEffort, int MaxOutputTokens, int? ContextWindow = null)
{
    /// <summary>
    /// An Ollama model, from appsettings.json's Retrieval settings. A lookup never reasons - a reasoning model can spend
    /// its whole output budget thinking about a simple question. A synthesis question gets the configured effort only
    /// when <paramref name="supportsThinking"/>: Ollama doesn't ignore a think request for a model that can't reason, it
    /// throws ("&lt;model&gt; does not support thinking") - see OllamaSetup.ChatModelSupportsThinkingAsync. Its window is
    /// <paramref name="contextLength"/>, the num_ctx the app sends (Ollama:ContextLength).
    /// </summary>
    public static ChatModelOptions ForOllama(RetrievalSettings retrieval, bool supportsThinking, int contextLength) => new(
        retrieval.ChatTemperature,
        ReasoningEffort.None,
        supportsThinking ? retrieval.ReasoningEffort : ReasoningEffort.None,
        retrieval.MaxOutputTokens,
        contextLength);
}
