using Anthropic;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using RagFilingExplorer.Local;
using RagFilingExplorer.Local.Retrieval;

namespace RagFilingExplorer.Claude;

/// <summary>
/// Claude as the answer service's chat model, through the SDK's Microsoft.Extensions.AI adapter. Its options send no
/// temperature - current models reject a non-default one, so answers aren't fixed by temperature 0 as llama's are; a
/// run is fixed by the evaluation's response cache instead. No server-side refusal fallback either: an answer from
/// another model would be measured as this one's.
/// </summary>
internal static class ClaudeChat
{
    /// <summary>
    /// The client and its options, after checking the key is set and the model exists (the Models API - no tokens
    /// billed), so a typo fails at startup rather than on the first question.
    /// </summary>
    public static async Task<(IChatClient Client, ChatModelOptions Options)> CreateAsync(ClaudeSettings claude)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN")))
        {
            throw new StartupException(
                "No Claude API key: set the ANTHROPIC_API_KEY environment variable (never in a settings file - the repository is public).");
        }

        AnthropicClient client = new();
        try
        {
            await client.Models.Retrieve(claude.Model);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new StartupException($"The Claude API rejected the key in ANTHROPIC_API_KEY ({ex.Message}).", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new StartupException($"No Claude model '{claude.Model}' - check {ClaudeSettings.Section}:Model in {ClaudeSettings.FileName}.", ex);
        }

        ChatModelOptions options = new(Temperature: null, claude.LookupEffort, claude.SynthesisEffort, claude.MaxOutputTokens);
        return (client.AsIChatClient(claude.Model, claude.MaxOutputTokens), options);
    }
}
