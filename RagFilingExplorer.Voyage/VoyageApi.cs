using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using RagFilingExplorer.Core;

namespace RagFilingExplorer.Voyage;

/// <summary>
/// What Voyage's embedder and reranker share: the key (never a setting - <see cref="ReadKey"/>), the client, the retried
/// POST and the startup check. The endpoint is fixed.
/// </summary>
internal static class VoyageApi
{
    /// <summary>The environment variable the key can come from, ahead of user secrets.</summary>
    public const string EnvironmentKeyName = "VOYAGE_API_KEY";

    /// <summary>The key's name in user secrets.</summary>
    public const string SecretsKeyName = "Voyage:ApiKey";

    /// <summary>Attempts per request: a 429 (rate limit) or a 5xx is retried with exponential back-off, as Voyage's error
    /// codes page advises; any other failure is not.</summary>
    internal const int MaxAttempts = 8;

    private static readonly Uri BaseAddress = new("https://api.voyageai.com/v1/");

    private static readonly TimeSpan MaxBackOff = TimeSpan.FromSeconds(60);

    // Voyage's fields are snake_case (input_type, relevance_score, total_tokens); the web defaults keep reading
    // case-insensitive, as the HttpClient JSON helpers do without options. A response missing a field, or null where the
    // record says it can't be, fails to read rather than binding as null and failing later.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    /// <summary>
    /// The key from the app's configuration folder: VOYAGE_API_KEY, else Voyage:ApiKey from this library's .NET user secrets
    /// (<c>dotnet user-secrets set Voyage:ApiKey ... --project RagFilingExplorer.Voyage</c> - a file in the user's profile,
    /// outside the repository); null when neither is set.
    /// </summary>
    public static string? LoadKey(string basePath) => ReadKey(new ConfigurationBuilder()
        .SetBasePath(basePath)
        .AddJsonFile(VoyageSettings.FileName, optional: false)
        .AddUserSecrets(typeof(VoyageApi).Assembly, optional: true)
        .AddEnvironmentVariables()
        .Build());

    /// <summary>The key from the app's settings in a repository checkout - how the evaluation reads it.</summary>
    public static string? LoadKeyFromRepository(DirectoryInfo repoRoot) => LoadKey(Path.Combine(repoRoot.FullName, "RagFilingExplorer.Cli"));

    /// <summary>The key from a configuration - refused when appsettings.json holds it, since the repository is public.</summary>
    public static string? ReadKey(IConfiguration configuration) =>
        AppSettings.ReadApiKey(configuration, VoyageSettings.FileName, EnvironmentKeyName, SecretsKeyName);

    /// <summary>A client sending the key, after checking there is one and that it's printable.</summary>
    public static HttpClient CreateClient(string? apiKey)
    {
        if (apiKey is null)
        {
            throw new StartupException(
                $"No Voyage API key: dotnet user-secrets set {SecretsKeyName} <key> --project RagFilingExplorer.Voyage, or the "
                + $"{EnvironmentKeyName} environment variable - never in a settings file, the repository is public.");
        }

        AppSettings.EnsurePrintableKey(apiKey, "Voyage API");

        // The index build embeds one chunk per request (VectorStore:UpsertBatchSize), a rerank scores one question's
        // candidates - each well inside a minute.
        HttpClient http = new() { BaseAddress = BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    /// <summary>
    /// One startup call - a wrong key, model or network fails before any work, as a <see cref="StartupException"/> naming
    /// <paramref name="setting"/>.
    /// </summary>
    public static async Task CheckAsync(Func<Task> call, string setting)
    {
        try
        {
            await call();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new StartupException($"Voyage rejected the key from user secrets' {SecretsKeyName} (or {EnvironmentKeyName} when set): {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            throw new StartupException($"Voyage's check of {setting} failed: {ex.Message}", ex);
        }
    }

    /// <summary>POSTs <paramref name="request"/> as JSON to <paramref name="path"/>, retrying a 429 or 5xx after
    /// <see cref="BackOff"/>; any other failure throws with Voyage's own detail.</summary>
    public static async Task<TResponse> PostAsync<TResponse>(
        HttpClient http, string path, object request, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            // Declared as object, the request is serialized as its runtime type.
            using HttpResponseMessage response = await http.PostAsJsonAsync(path, request, Json, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<TResponse>(Json, cancellationToken)
                        ?? throw new InvalidOperationException("Voyage returned an empty response.");
                }
                catch (JsonException ex)
                {
                    // An InvalidOperationException, so the startup check reports it as Voyage's (CheckAsync).
                    throw new InvalidOperationException($"Voyage's {path} response isn't in its documented shape: {ex.Message}", ex);
                }
            }

            bool retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retryable || attempt == MaxAttempts)
            {
                // Voyage's 4xx body is {"detail": "..."}; a 5xx may have none.
                string body = await response.Content.ReadAsStringAsync(cancellationToken);
                string detail = TryReadDetail(body) ?? body;
                throw new HttpRequestException(
                    $"Voyage answered {(int)response.StatusCode} {response.StatusCode}{(retryable ? $" after {MaxAttempts} attempts" : "")}: {detail}",
                    inner: null, response.StatusCode);
            }

            await delay(BackOff(attempt), cancellationToken);
        }
    }

    /// <summary>1, 2, 4 ... seconds, at most <see cref="MaxBackOff"/> - about two minutes over every attempt.</summary>
    internal static TimeSpan BackOff(int attempt) => TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), MaxBackOff.TotalSeconds));

    private static string? TryReadDetail(string body)
    {
        try
        {
            return JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out JsonElement detail) ? detail.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
