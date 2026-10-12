using Microsoft.Extensions.AI;
using OllamaSharp;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Retrieval;

namespace RagFilingExplorer.Ollama;

/// <summary>Startup-time Ollama plumbing: the clients, the readiness check, and the capability check.</summary>
internal static class OllamaSetup
{
    /// <summary>One client for embeddings and one for chat, each with the configured timeout.</summary>
    public static (OllamaApiClient Embedding, OllamaApiClient Chat) CreateClients(OllamaSettings ollama) =>
        (new OllamaApiClient(CreateHttpClient(ollama), ollama.EmbeddingModel),
         new OllamaApiClient(CreateHttpClient(ollama), ollama.ChatModel));

    /// <summary>The configured Ollama chat model's options - its thinking capability asked of Ollama itself.</summary>
    public static async Task<ChatModelOptions> ReadChatModelAsync(AppSettings settings, OllamaApiClient chat) =>
        ChatModelOptions.ForOllama(settings.Retrieval, await ChatModelSupportsThinkingAsync(chat, settings.Ollama.ChatModel));

    /// <summary>
    /// <see cref="AppComposition.OpenExistingIndexAsync"/> embedding with the configured Ollama model, once Ollama is
    /// ready. The configured Ollama chat model answers unless <paramref name="chatClient"/> and <paramref name="chatModel"/>
    /// say otherwise - an evaluation passes a client that caches the model's responses.
    /// </summary>
    public static async Task<RagRuntime> OpenExistingIndexAsync(
        AppSettings settings, DirectoryInfo repoRoot, IChatClient? chatClient = null, ChatModelOptions? chatModel = null)
    {
        (OllamaApiClient embedding, OllamaApiClient chat) = CreateClients(settings.Ollama);
        await EnsureReadyAsync(chat, settings.Ollama, [settings.Ollama.EmbeddingModel, settings.Ollama.ChatModel]);
        return await AppComposition.OpenExistingIndexAsync(
            settings, repoRoot, embedding, chatClient ?? chat, chatModel ?? await ReadChatModelAsync(settings, chat));
    }

    // Default HttpClient.Timeout (100s) isn't enough for CPU-only Ollama inference - a batch upsert of
    // even a few dozen chunks, or a single llama3.1:8b generation over several chunks of context, can
    // easily exceed it.
    private static HttpClient CreateHttpClient(OllamaSettings ollama) =>
        new() { BaseAddress = new Uri(ollama.BaseUrl), Timeout = TimeSpan.FromMinutes(ollama.TimeoutMinutes) };

    // Fails fast, before any chunking, with the exact fix - otherwise a cloner without Ollama running (or
    // without a model pulled) gets a raw HttpRequestException stack trace, and during a first build only
    // after every filing has already been converted. Checks only the models given: the ones this run uses.
    public static async Task EnsureReadyAsync(OllamaApiClient client, OllamaSettings ollama, IEnumerable<string> models)
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
        string[] missing = models
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
