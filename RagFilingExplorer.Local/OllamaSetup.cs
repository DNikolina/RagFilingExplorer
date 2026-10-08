using OllamaSharp;

namespace RagFilingExplorer.Local;

/// <summary>Startup-time Ollama plumbing: the HTTP client, the readiness check, and the capability check.</summary>
internal static class OllamaSetup
{
    // Default HttpClient.Timeout (100s) isn't enough for CPU-only Ollama inference - a batch upsert of
    // even a few dozen chunks, or a single llama3.1:8b generation over several chunks of context, can
    // easily exceed it.
    public static HttpClient CreateHttpClient(OllamaSettings ollama) =>
        new() { BaseAddress = new Uri(ollama.BaseUrl), Timeout = TimeSpan.FromMinutes(ollama.TimeoutMinutes) };

    // Fails fast, before any chunking, with the exact fix - otherwise a cloner without Ollama running (or
    // without a model pulled) gets a raw HttpRequestException stack trace, and during a first build only
    // after every filing has already been converted.
    public static async Task EnsureReadyAsync(OllamaApiClient client, OllamaSettings ollama)
    {
        List<string> installed;
        try
        {
            installed = (await client.ListLocalModelsAsync()).Select(m => m.Name).ToList();
        }
        catch (HttpRequestException ex)
        {
            throw new StartupException(
                $"Could not reach Ollama at {ollama.BaseUrl} ({ex.Message}). Is it installed and running? "
                + "Start it with 'ollama serve' or the Ollama app.", ex);
        }

        // Ollama reports an untagged pull ("nomic-embed-text") as "nomic-embed-text:latest".
        string[] missing = new[] { ollama.EmbeddingModel, ollama.ChatModel }
            .Where(model => !installed.Any(name => name == model || name == $"{model}:latest"))
            .Distinct()
            .ToArray();

        if (missing.Length > 0)
        {
            throw new StartupException(
                $"Ollama is running but doesn't have these model(s) pulled: {string.Join(", ", missing)}. Run: "
                + string.Join(" && ", missing.Select(m => $"ollama pull {m}")));
        }
    }

    // Ollama doesn't quietly ignore a "think" request for a model that can't reason - it throws a hard
    // OllamaException ("<model> does not support thinking"), which would end the session on the first
    // synthesis question. Checked once at startup via Ollama's own /api/show capabilities list, rather than
    // assumed, so ChatModelOptions.ForOllama only ever asks for reasoning from a model that genuinely supports it.
    public static async Task<bool> ChatModelSupportsThinkingAsync(OllamaApiClient client, string model)
    {
        OllamaSharp.Models.ShowModelResponse info = await client.ShowModelAsync(model);
        return info.Capabilities?.Contains("thinking") ?? false;
    }
}
