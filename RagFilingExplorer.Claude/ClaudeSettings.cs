using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using RagFilingExplorer.Local;

namespace RagFilingExplorer.Claude;

/// <summary>
/// claudesettings.json's "Claude" section: which Claude model answers and how. Chunking, retrieval and the index stay
/// Local's (appsettings.json, read unchanged), so only the model differs from the local app. Every key is required, as
/// in appsettings.json; an environment variable of the same name overrides one for a single run
/// (<c>Claude__Model=claude-sonnet-5-5</c>). The API key is never one of them (<see cref="ApiKey"/>).
/// </summary>
internal sealed class ClaudeSettings
{
    public const string FileName = "claudesettings.json";
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
    /// The API key: the ANTHROPIC_API_KEY environment variable, else Claude:ApiKey from .NET user secrets
    /// (<c>dotnet user-secrets set Claude:ApiKey ... --project RagFilingExplorer.Claude</c> - a file in the user's profile,
    /// outside the repository); null when neither is set. Read-only, so it isn't a required key, and refused in
    /// claudesettings.json - the repository is public.
    /// </summary>
    public string? ApiKey => apiKey;

    private string? apiKey;

    /// <summary>The project's own settings in a repository checkout - how the evaluation reads them.</summary>
    public static ClaudeSettings LoadFromRepository(DirectoryInfo repoRoot) => Load(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Claude"));

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
        AppSettings.EnsureKeysPresent(configuration, typeof(ClaudeSettings), Section, FileName);
        ClaudeSettings settings = configuration.GetSection(Section).Get<ClaudeSettings>()
            ?? throw new InvalidOperationException($"{FileName} failed to bind its {Section} section.");

        // Both names the key is read under - a root "ANTHROPIC_API_KEY" in the file would be read too, and committed with it.
        string[] keyNames = [EnvironmentKeyName, $"{Section}:{nameof(ApiKey)}"];
        // This file's provider only: user secrets are a JSON file too (secrets.json), and that one is where the key belongs.
        string? inFile = configuration is IConfigurationRoot root
            ? keyNames.FirstOrDefault(k => root.Providers.OfType<JsonConfigurationProvider>().Any(p => p.Source.Path == FileName && p.TryGet(k, out _)))
            : null;
        if (inFile is not null)
        {
            throw new InvalidOperationException(
                $"{FileName} holds {inFile} - remove it, the repository is public; keep the key in user secrets or ANTHROPIC_API_KEY.");
        }

        settings.apiKey = keyNames.Select(k => configuration[k]).FirstOrDefault(k => !string.IsNullOrEmpty(k));

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

        return settings;
    }
}
