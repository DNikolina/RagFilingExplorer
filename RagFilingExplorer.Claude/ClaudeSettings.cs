using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core;

namespace RagFilingExplorer.Claude;

/// <summary>
/// appsettings.json's "Claude" section, read when Providers:Chat is Claude: which Claude model answers and how. Chunking,
/// retrieval and the index are the same as for any chat model, so only the model differs. Every key is required; an
/// environment variable of the same name overrides one for a single run (<c>Claude__Model=claude-sonnet-5-5</c>). The
/// API key is never one of them (<see cref="ApiKey"/>).
/// </summary>
internal sealed class ClaudeSettings
{
    public const string FileName = "appsettings.json";
    public const string Section = "Claude";

    /// <summary>The environment variable the key can come from, ahead of user secrets.</summary>
    public const string EnvironmentKeyName = "ANTHROPIC_API_KEY";

    /// <summary>An exact model id from Anthropic's model list (claude-opus-5-5, claude-sonnet-5-5, claude-haiku-5-5),
    /// checked against the Models API at startup.</summary>
    public required string Model { get; set; }

    /// <summary>
    /// The reasoning effort for a single-fact lookup and for a question QueryIntentResolver.RequiresSynthesis flags:
    /// Low, Medium, High or ExtraHigh. Never None - the SDK sends it as thinking disabled, which current Opus and Sonnet
    /// models reject with a 400; effort is their only control over thinking.
    /// </summary>
    public required ReasoningEffort LookupEffort { get; set; }

    /// <inheritdoc cref="LookupEffort"/>
    public required ReasoningEffort SynthesisEffort { get; set; }

    /// <summary>
    /// The API's max_tokens: thinking and answer together, since thinking counts toward it. No context window to share,
    /// unlike Ollama's num_ctx (Claude's is 1M), and only the tokens used are billed - the ceiling is there so a starved
    /// response fails (RagAnswerService's guard) instead of running long.
    /// </summary>
    public required int MaxOutputTokens { get; set; }

    /// <summary>
    /// How many retrieved excerpts Claude reads - replaces appsettings.json's Retrieval:GenerationTopK, which is sized for
    /// Ollama's 4,096-token num_ctx; Claude's window is 1M. Applied by <see cref="ApplyTo"/>. At most
    /// Retrieval:VerboseSearchTopK, the depth the evaluation retrieves and ranks within. 8, not llama's 5: what Claude
    /// misses is an answer ranked just past its excerpts, and three more cost little against a whole prompt - the runs
    /// at 5, 8 and 12: docs/Decision-Log.md, "paid services (v4)".
    /// </summary>
    public required int GenerationTopK { get; set; }

    /// <summary>
    /// The API key: the ANTHROPIC_API_KEY environment variable, else Claude:ApiKey from .NET user secrets
    /// (<c>dotnet user-secrets set Claude:ApiKey ... --project RagFilingExplorer.Claude</c> - a file in the user's profile,
    /// outside the repository; this library's own, so the evaluation finds it too); null when neither is set. Read-only,
    /// so it isn't a required key, and refused in appsettings.json - the repository is public.
    /// </summary>
    public string? ApiKey => apiKey;

    private string? apiKey;

    /// <summary>The app's settings in a repository checkout - how the evaluation reads them.</summary>
    public static ClaudeSettings LoadFromRepository(DirectoryInfo repoRoot) => Load(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Cli"));

    /// <summary>The settings file, the user secrets of this assembly (so the evaluation, loading it, finds the same key),
    /// then environment variables.</summary>
    public static ClaudeSettings Load(string basePath) => From(new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile(FileName, optional: false)
        .AddUserSecrets(typeof(ClaudeSettings).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>The settings from a configuration - every key present, every value valid, or an error naming it.</summary>
    public static ClaudeSettings From(IConfiguration configuration)
    {
        AppSettings.EnsureKeysValid(configuration, typeof(ClaudeSettings), Section, FileName);
        ClaudeSettings settings = configuration.GetSection(Section).Get<ClaudeSettings>()
            ?? throw new InvalidOperationException($"{FileName} failed to bind its {Section} section.");

        settings.apiKey = AppSettings.ReadApiKey(configuration, FileName, EnvironmentKeyName, $"{Section}:{nameof(ApiKey)}");

        if (settings.LookupEffort == ReasoningEffort.None || settings.SynthesisEffort == ReasoningEffort.None)
        {
            throw new InvalidOperationException(
                $"{FileName}: {Section}:LookupEffort and {Section}:SynthesisEffort can't be None - current Claude models reject "
                + "thinking disabled; use Low to think least.");
        }

        if (settings.MaxOutputTokens <= 0)
        {
            throw new InvalidOperationException($"{FileName}: {Section}:MaxOutputTokens must be positive.");
        }

        if (settings.GenerationTopK <= 0)
        {
            throw new InvalidOperationException($"{FileName}: {Section}:GenerationTopK must be positive.");
        }

        return settings;
    }

    /// <summary>
    /// Sets the retrieval settings (appsettings.json) to read <see cref="GenerationTopK"/> excerpts. The interactive search depth
    /// (DefaultSearchTopK) is raised to it too: RagAnswerService takes the excerpts from the search's results, so a
    /// shallower search would silently give fewer.
    /// </summary>
    public void ApplyTo(RetrievalSettings retrieval)
    {
        if (GenerationTopK > retrieval.VerboseSearchTopK)
        {
            throw new InvalidOperationException(
                $"{FileName}: {Section}:GenerationTopK ({GenerationTopK}) is more than appsettings.json's Retrieval:VerboseSearchTopK "
                + $"({retrieval.VerboseSearchTopK}), the deepest search.");
        }

        retrieval.GenerationTopK = GenerationTopK;
        retrieval.DefaultSearchTopK = Math.Max(retrieval.DefaultSearchTopK, GenerationTopK);
    }
}
