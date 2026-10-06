using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Local.Chunking;
using RagFilingExplorer.Local.Retrieval;

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
/// Retrieval-relevant domain logic that isn't just a number (the statement-type keyword lists in
/// QueryIntentResolver, the section-boundary/statement-title regexes) stays in code on purpose: a typo there
/// would silently break filtering with no compiler to catch it. Company names aren't configured at all - each
/// filing registers its own from its cover facts (CompanyRegistry).
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
    /// Every leaf key this type binds (e.g. "Ollama:ChatModel"), derived by reflection - a hand-maintained list
    /// could miss a new setting and silently reopen the "missing key binds as null" gap the check exists to close.
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
    // Which IChunkingStrategy builds the index - bound straight to the enum, so a typo fails at startup.
    // Each strategy has its own rag.<strategy>.db and chunk-review/<strategy>/, so switching is instant
    // once each has been built.
    public required ChunkingStrategyKind Strategy { get; set; }
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

    // Vector (the statement type is a hard filter) or Hybrid (vectors + FTS5 keywords, fused, the statement type a
    // boost) - bound straight to the enum, so a typo fails at startup. See RagAnswerService.
    public required SearchMode Search { get; set; }

    // How deep each of hybrid search's ranked lists goes before they're fused. Unused by Vector search.
    public required int HybridCandidates { get; set; }

    // Rerank each company's top RerankCandidates hybrid candidates with a local cross-encoder before the
    // top-K is cut (CrossEncoderReranker). Hybrid only - it was measured on hybrid candidates. The model lives outside
    // the repo, in RerankModelDirectory (environment variables expanded), and is refused unless its SHA-256 matches.
    public required bool Rerank { get; set; }
    public required int RerankCandidates { get; set; }
    public required string RerankModelDirectory { get; set; }
    public required string RerankModelSha256 { get; set; }

    // One of Microsoft.Extensions.AI's ReasoningEffort enum names (None, Low, Medium, High, ExtraHigh) -
    // bound straight to the enum, so a typo fails at startup rather than when it's first used.
    // NOT applied to every question - only ones QueryIntentResolver.RequiresSynthesis flags as needing
    // multi-step reasoning (comparisons, ratios, trends). A single-fact lookup always gets Effort.None
    // regardless of this setting: on a simple lookup with this app's long retrieved-context prompt, a reasoning
    // model can spend its entire generation budget thinking and never produce an answer. See
    // RagAnswerService.AskAsync for the routing logic, and MaxOutputTokens below for the other half.
    public required ReasoningEffort ReasoningEffort { get; set; }

    // Ceiling for ChatOptions.MaxOutputTokens (Ollama's num_predict), applied to every question and every
    // chat model - set explicitly rather than left to Ollama's default, which a thinking model can exhaust
    // silently. Bounded by the context window: prompt + this must fit Ollama's num_ctx (4096 by default; this
    // app doesn't set it) - see appsettings.json for the measured sizing. A model that still hits this ceiling
    // without producing answer text fails loudly (RagAnswerService's starved-response guard).
    public required int MaxOutputTokens { get; set; }
}
