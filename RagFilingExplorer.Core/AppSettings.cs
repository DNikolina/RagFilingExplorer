using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core.Chunking;
using RagFilingExplorer.Core.Retrieval;

namespace RagFilingExplorer.Core;

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
    public required ProviderSettings Providers { get; set; }
    public required EmbeddingSettings Embedding { get; set; }
    public required OllamaSettings Ollama { get; set; }
    public required ChunkingSettings Chunking { get; set; }
    public required VectorStoreSettings VectorStore { get; set; }
    public required RetrievalSettings Retrieval { get; set; }

    public static AppSettings Load(string basePath)
    {
        // An environment variable of the same name overrides a key for one run (Embedding__Model=embeddinggemma),
        // as in the evaluation's settings - the file stays the one source of every value.
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddEnvironmentVariables()
            .Build();

        AppSettings settings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("appsettings.json is missing or failed to bind to AppSettings.");

        EnsureKeysPresent(configuration, typeof(AppSettings), prefix: null, "appsettings.json");
        return settings;
    }

    /// <summary>
    /// Fails naming every key <paramref name="type"/> binds (under <paramref name="prefix"/>) that the configuration
    /// lacks - the evaluation's settings use it too. Checks presence against the raw configuration tree rather than the
    /// bound values, so a legitimately-zero setting (e.g. OverlapTokens: 0, ChatTemperature: 0) is never mistaken for
    /// "missing" - see the class summary for why `required` alone doesn't catch this.
    /// </summary>
    internal static void EnsureKeysPresent(IConfiguration configuration, Type type, string? prefix, string fileName)
    {
        string[] missing = LeafKeys(type, prefix).Where(key => configuration[key] is null).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"{fileName} is missing required key(s): {string.Join(", ", missing)}.");
        }
    }

    /// <summary>
    /// Every leaf key this type binds (e.g. "Ollama:ChatModel"), derived by reflection - a hand-maintained list
    /// could miss a new setting and silently reopen the "missing key binds as null" gap the check exists to close.
    /// </summary>
    internal static IEnumerable<string> RequiredConfigurationKeys() => LeafKeys(typeof(AppSettings), prefix: null);

    // Settable properties only: a read-only one is computed from the others, not bound.
    private static IEnumerable<string> LeafKeys(Type type, string? prefix)
    {
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
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

/// <summary>
/// Who embeds the chunks and questions, and who answers - each chosen apart from the other, so any provider can pair with
/// any other. Ollama for both (the default) is free and needs no key; a paid provider is used only when chosen here, and
/// then needs its key (user secrets or an environment variable, never this file). Only the chosen providers are checked
/// at startup. The evaluation chooses its own chat model (evalsettings.json's ChatModel).
/// </summary>
internal sealed class ProviderSettings
{
    /// <summary>Ollama or Voyage (every chunk and question billed), serving the Embedding section's model - which must be
    /// one of that provider's (EmbeddingTemplates).</summary>
    public required EmbeddingProvider Embedding { get; set; }

    /// <summary>Ollama (the Ollama section's ChatModel) or Claude (the Claude section; every answer billed).</summary>
    public required ChatProvider Chat { get; set; }
}

internal enum EmbeddingProvider
{
    Ollama,
    Voyage,
}

internal enum ChatProvider
{
    Ollama,
    Claude,
}

internal sealed class EmbeddingSettings
{
    /// <summary>
    /// The embedding model, by its provider's name for it - apart from the provider's own section, since it decides the
    /// index: each model has its own (rag.&lt;strategy&gt;.&lt;model&gt;.db; nomic-embed-text keeps rag.&lt;strategy&gt;.db),
    /// with the query/document templates and vector size from its model card. An unlisted model is refused at startup -
    /// see EmbeddingTemplates.
    /// </summary>
    public required string Model { get; set; }
}

internal sealed class OllamaSettings
{
    public required string BaseUrl { get; set; }
    public required int TimeoutMinutes { get; set; }
    public required string ChatModel { get; set; }
}

internal sealed class ChunkingSettings
{
    /// <summary>
    /// Which chunking strategy builds (and answers from) the index - bound straight to the enum, so a typo fails at
    /// startup. <c>Structured</c> (the default) reads the filing's DOM and inline XBRL: statement types from the filer's
    /// Statement roles, note topics, period labels, the company from the cover facts; no markitdown. v1's two are kept
    /// as the reference: <c>Markdown</c> (markitdown tables, split with headers repeated) and <c>Linearized</c> (tables
    /// turned into self-contained row lines from the HTML first). Each strategy has its own rag.&lt;strategy&gt;.db and
    /// chunk-review/&lt;strategy&gt;/, so switching back and forth needs no rebuild once each has been built.
    /// </summary>
    public required ChunkingStrategyKind Strategy { get; set; }
    public required string TokenizerModel { get; set; }
    public required int MaxTokensPerChunk { get; set; }
    public required int OverlapTokens { get; set; }
}

internal sealed class VectorStoreSettings
{
    /// <summary>
    /// Keep at 1 while CommunityToolkit.VectorData.SqliteVec is pinned to 1.0.1-preview (see the .csproj): any
    /// multi-record UpsertAsync batch throws "UNIQUE constraint failed on vec_chunks primary key" - a known upstream
    /// sqlite-vec bug, fixed in a newer native build the NuGet package hasn't picked up. docs/Design-FAQ.md, "Why is
    /// VectorStore.UpsertBatchSize 1?".
    /// </summary>
    public required int UpsertBatchSize { get; set; }
}

internal sealed class RetrievalSettings
{
    public required int DefaultSearchTopK { get; set; }
    public required int VerboseSearchTopK { get; set; }
    public required int GenerationTopK { get; set; }
    public required float ChatTemperature { get; set; }

    /// <summary>
    /// <c>Hybrid</c> (the default): a vector search and an SQLite FTS5 keyword search (bm25) fused by reciprocal rank
    /// fusion, the statement type a third, boosting list instead of a filter - so a question routed to the wrong
    /// statement can still reach its answer. <c>Vector</c>: one vector search, the question's statement type (if any) a
    /// hard filter - v1's retrieval, and what the Markdown and Linearized strategies were measured with. The company
    /// filter stays hard in both. Bound straight to the enum, so a typo fails at startup. See RagAnswerService and
    /// docs/Decision-Log.md, "Step 2 - hybrid search".
    /// </summary>
    public required SearchMode Search { get; set; }

    /// <summary>
    /// How many candidates each of hybrid search's ranked lists contributes before fusion; unused by Vector search. Deep
    /// enough to match fusing complete rankings - docs/Decision-Log.md, "Step 2 - hybrid search".
    /// </summary>
    public required int HybridCandidates { get; set; }

    /// <summary>
    /// Rerank each company's top <see cref="RerankCandidates"/> hybrid candidates with a local cross-encoder before the
    /// top-K is cut (CrossEncoderReranker). Hybrid only - it was measured on hybrid candidates. Off by default: built
    /// and measured, then not kept - hybrid alone puts as many answers in the model's context (docs/Decision-Log.md,
    /// "A1-A27 with reranking off" onwards).
    /// </summary>
    public required bool Rerank { get; set; }

    /// <summary>How many hybrid candidates per company the reranker reorders - chosen in docs/Decision-Log.md, "Step 2b
    /// spike - measured".</summary>
    public required int RerankCandidates { get; set; }

    /// <summary>Where the reranker's model lives, outside the repo (environment variables expanded):
    /// ms-marco-MiniLM-L6-v2's onnx/model.onnx and vocab.txt, fetched separately (docs/Decision-Log.md, "Step 2b
    /// resumed").</summary>
    public required string RerankModelDirectory { get; set; }

    /// <summary>The SHA-256 the model.onnx in <see cref="RerankModelDirectory"/> must match, or it's refused.</summary>
    public required string RerankModelSha256 { get; set; }

    /// <summary>
    /// One of Microsoft.Extensions.AI's ReasoningEffort enum names (None, Low, Medium, High, ExtraHigh) - bound straight
    /// to the enum, so a typo fails at startup. Applied only to questions QueryIntentResolver.RequiresSynthesis flags as
    /// needing multi-step reasoning (comparisons, ratios, trends); a single-fact lookup always gets Effort.None: on a
    /// simple lookup with this app's long retrieved-context prompt, a reasoning model can spend its entire generation
    /// budget thinking and never produce an answer. Safe to leave non-None with a non-reasoning model such as
    /// llama3.1:8b: a think request is only sent to a model whose Ollama /api/show lists the capability, since Ollama
    /// hard-rejects one otherwise. The level reaches Ollama as the request's "think" field, and only a model whose
    /// /api/show "thinking.values" lists named levels (gpt-oss: low, medium, high) honours it; a model listing only
    /// false/true (qwen3.5) resolves any level to its default, on - so for it only None versus any other value matters.
    /// See RagAnswerService.AskAsync, <see cref="MaxOutputTokens"/> for the other half, and docs/Decision-Log.md,
    /// "Follow-up: reasoning-model support".
    /// </summary>
    public required ReasoningEffort ReasoningEffort { get; set; }

    /// <summary>
    /// Ceiling for the model's total output (ChatOptions.MaxOutputTokens, Ollama's num_predict; thinking + answer for a
    /// reasoning model), for every question and every chat model - set explicitly rather than left to Ollama's default,
    /// which a thinking model can exhaust silently. The prompt and this output share Ollama's context window (num_ctx,
    /// not set by this app: 4096 by default), so prompt + this must stay under 4096 - past it, Ollama silently drops
    /// the oldest tokens: the system prompt and the top-ranked chunks. The value leaves room for several times the longest
    /// answer seen (docs/Decision-Log.md, "pre-manual-pass review"). A reasoning model gets little room
    /// to think within this; giving it more means raising num_ctx too. A model that still hits this ceiling without
    /// producing answer text fails loudly (RagAnswerService's starved-response guard).
    /// </summary>
    public required int MaxOutputTokens { get; set; }
}
