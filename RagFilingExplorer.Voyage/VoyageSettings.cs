using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// appsettings.json's "Voyage" section, read when Voyage reranks (Retrieval:Rerank on, Providers:Rerank Voyage). The
/// embedder needs none of it - its model is Embedding:Model, which decides the index. Every key is required; an
/// environment variable of the same name overrides one for a single run (<c>Voyage__RerankModel=rerank-3-lite</c>). The
/// API key is never one of them (<see cref="VoyageApi.ReadKey"/>).
/// </summary>
internal sealed class VoyageSettings
{
    public const string FileName = "appsettings.json";
    public const string Section = "Voyage";

    /// <summary>
    /// The reranker, by Voyage's name for it: rerank-3 (its most accurate) or rerank-3-lite (faster, cheaper) - 200M free
    /// tokens each, then billed per token (query tokens x candidates + the candidates' tokens). Checked with one call at
    /// startup. docs/Decision-Log.md, "Batch 4 - a paid reranker".
    /// </summary>
    public required string RerankModel { get; set; }

    /// <summary>The app's settings in a repository checkout - how the evaluation reads them.</summary>
    public static VoyageSettings LoadFromRepository(DirectoryInfo repoRoot) => Load(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Cli"));

    public static VoyageSettings Load(string basePath) => From(new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile(FileName, optional: false)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>The settings from a configuration - every key present, or an error naming it.</summary>
    public static VoyageSettings From(IConfiguration configuration)
    {
        AppSettings.EnsureKeysPresent(configuration, typeof(VoyageSettings), Section, FileName);
        return configuration.GetSection(Section).Get<VoyageSettings>()
            ?? throw new InvalidOperationException($"{FileName} failed to bind its {Section} section.");
    }
}
