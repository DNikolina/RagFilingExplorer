using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace RagFilingExplorer.Local;

/// <summary>
/// Binds to appsettings.json - the tunable knobs (model names, chunk size, timeouts, top-K), things
/// someone might reasonably want to change without touching logic. Every property is <c>required</c> so
/// appsettings.json is the single source of truth: there's no separate "default" value in code that
/// could quietly drift out of sync with it. Note that <c>required</c> alone does NOT make a missing key
/// fail at bind time - <see cref="Microsoft.Extensions.Configuration.ConfigurationBinder"/> populates
/// this type via reflection, not an object initializer, so it never checks required members; <see cref="Load"/>
/// does that check explicitly, against the raw configuration keys.
///
/// Retrieval-relevant domain logic that isn't just a number (the company-name and statement-type
/// keyword lists in QueryIntentResolver, the section-boundary/statement-title regexes) stays in code on
/// purpose: a typo there would silently break filtering with no compiler to catch it.
/// </summary>
internal sealed class AppSettings
{
    public required OllamaSettings Ollama { get; set; }
    public required ChunkingSettings Chunking { get; set; }
    public required VectorStoreSettings VectorStore { get; set; }
    public required RetrievalSettings Retrieval { get; set; }

    public static AppSettings Load(string basePath)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        AppSettings settings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("appsettings.json is missing or failed to bind to AppSettings.");

        EnsureAllKeysPresent(configuration);
        return settings;
    }

    // Checks presence against the raw configuration tree rather than the bound values, specifically so a
    // legitimately-zero setting (e.g. OverlapTokens: 0, ChatTemperature: 0) is never mistaken for
    // "missing" - see the class summary for why `required` alone doesn't catch this.
    private static void EnsureAllKeysPresent(IConfiguration configuration)
    {
        string[] missing = RequiredConfigurationKeys().Where(key => configuration[key] is null).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"appsettings.json is missing required key(s): {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Every leaf key this type binds (e.g. "Ollama:ChatModel"), derived by reflection. This used to be a
    /// hand-maintained list in Program.cs, which meant adding a setting and forgetting the list would
    /// silently reintroduce the "missing key binds as null" gap the check exists to close.
    /// </summary>
    internal static IEnumerable<string> RequiredConfigurationKeys() => LeafKeys(typeof(AppSettings), prefix: null);

    private static IEnumerable<string> LeafKeys(Type type, string? prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            string key = prefix is null ? property.Name : $"{prefix}:{property.Name}";
            bool isSection = property.PropertyType.IsClass && property.PropertyType != typeof(string);

            foreach (string leaf in isSection ? LeafKeys(property.PropertyType, key) : [key])
            {
                yield return leaf;
            }
        }
    }
}

internal sealed class OllamaSettings
{
    public required string BaseUrl { get; set; }
    public required int TimeoutMinutes { get; set; }
    public required string EmbeddingModel { get; set; }
    public required string ChatModel { get; set; }
}

internal sealed class ChunkingSettings
{
    public required string TokenizerModel { get; set; }
    public required int MaxTokensPerChunk { get; set; }
    public required int OverlapTokens { get; set; }
}

internal sealed class VectorStoreSettings
{
    public required int UpsertBatchSize { get; set; }
}

internal sealed class RetrievalSettings
{
    public required int DefaultSearchTopK { get; set; }
    public required int VerboseSearchTopK { get; set; }
    public required int GenerationTopK { get; set; }
    public required float ChatTemperature { get; set; }

    // One of Microsoft.Extensions.AI's ReasoningEffort enum names (None, Low, Medium, High, ExtraHigh) -
    // bound straight to the enum, so a typo fails at startup rather than when it's first used.
    // NOT applied to every question - only ones QueryIntentResolver.RequiresSynthesis flags as needing
    // multi-step reasoning (comparisons, ratios, trends). A single-fact lookup always gets Effort.None
    // regardless of this setting, since a reasoning model's "thinking" phase is wasted overhead on those
    // and was the direct cause of a real bug: qwen3.5:2b, tested as a reference model, burned its entire
    // generation budget on chain-of-thought for a simple lookup against this app's longer retrieved-context
    // prompt and never produced an answer. See RagAnswerService.AskAsync for the routing logic, and
    // MaxOutputTokens below for the other half of the fix (giving reasoning room to actually finish).
    public required ReasoningEffort ReasoningEffort { get; set; }

    // Ceiling for ChatOptions.MaxOutputTokens (Ollama's num_predict). Previously left unset, which meant
    // Ollama's own default governed - the same qwen3.5:2b bug above meant the model could exhaust that
    // default while thinking and never reach the answer, with no error, just silence. Sized generously
    // enough to cover a full reasoning trace plus the answer for a synthesis question; a simple lookup
    // uses nowhere near this much. See RagAnswerService's starved-response guard for what happens if a
    // model still hits this ceiling without producing real answer text - it now fails loudly instead.
    public required int MaxOutputTokens { get; set; }
}
