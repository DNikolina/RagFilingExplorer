using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.VectorStore;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// Voyage as the embedder, when appsettings.json's Providers:Embedding is Voyage: the model is Embedding:Model, and nothing
/// else is configured - the endpoint is fixed and the key is never a setting (<see cref="ReadKey"/>).
/// </summary>
internal static class VoyageEmbedding
{
    /// <summary>The environment variable the key can come from, ahead of user secrets.</summary>
    public const string EnvironmentKeyName = "VOYAGE_API_KEY";

    /// <summary>The key's name in user secrets.</summary>
    public const string SecretsKeyName = "Voyage:ApiKey";

    private const string SettingsFileName = "appsettings.json";

    private static readonly Uri BaseAddress = new("https://api.voyageai.com/v1/");

    /// <summary>
    /// The document generator (builds the index) and the query generator (searches it), sharing one client - after one
    /// query embedding of a single word, so a wrong key or model fails at startup rather than after chunking. That check
    /// is billed as a few tokens.
    /// </summary>
    public static async Task<(IEmbeddingGenerator<string, Embedding<float>> Documents, IEmbeddingGenerator<string, Embedding<float>> Queries)> CreateAsync(
        string? apiKey, EmbeddingTemplates model)
    {
        if (apiKey is null)
        {
            throw new StartupException(
                $"No Voyage API key: dotnet user-secrets set {SecretsKeyName} <key> --project RagFilingExplorer.Voyage, or the "
                + $"{EnvironmentKeyName} environment variable - never in a settings file, the repository is public.");
        }

        // A key is printable ASCII with no spaces; Ctrl+V at a hidden PowerShell prompt stores the control character itself.
        if (apiKey.Any(c => c is <= ' ' or > '~'))
        {
            throw new StartupException(
                $"The Voyage API key holds a space or non-printable character ({apiKey.Length} characters) - a failed paste? Set it again.");
        }

        // The index build embeds one chunk per request (VectorStore:UpsertBatchSize), each well inside a minute.
        HttpClient http = new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        VoyageEmbeddingGenerator queries = new(http, model.Model, "query", model.Dimensions);
        try
        {
            await queries.GenerateAsync(["revenue"]);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new StartupException($"Voyage rejected the key from user secrets' {SecretsKeyName} (or {EnvironmentKeyName} when set): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            throw new StartupException($"Voyage's check of Embedding:Model '{model.Model}' failed: {ex.Message}", ex);
        }

        return (new VoyageEmbeddingGenerator(http, model.Model, "document", model.Dimensions), queries);
    }

    /// <summary>
    /// The key from the app's configuration folder: VOYAGE_API_KEY, else Voyage:ApiKey from this library's .NET user secrets
    /// (<c>dotnet user-secrets set Voyage:ApiKey ... --project RagFilingExplorer.Voyage</c> - a file in the user's profile,
    /// outside the repository); null when neither is set.
    /// </summary>
    public static string? LoadKey(string basePath) => ReadKey(new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile(SettingsFileName, optional: false)
        .AddUserSecrets(typeof(VoyageEmbedding).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>The key from the app's settings in a repository checkout - how the evaluation reads it.</summary>
    public static string? LoadKeyFromRepository(DirectoryInfo repoRoot) => LoadKey(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Cli"));

    /// <summary>The key from a configuration - refused when appsettings.json holds it, since the repository is public.</summary>
    public static string? ReadKey(IConfiguration configuration) =>
        AppSettings.ReadApiKey(configuration, SettingsFileName, EnvironmentKeyName, SecretsKeyName);
}
