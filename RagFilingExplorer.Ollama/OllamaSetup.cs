using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;
using RagFilingExplorer.Core;
using RagFilingExplorer.Core.Retrieval;

namespace RagFilingExplorer.Ollama;

/// <summary>Startup-time Ollama plumbing: the clients, the readiness check, and the capability check.</summary>
internal static class OllamaSetup
{
    /// <summary>One client for embeddings and one for chat, each with the configured timeout.</summary>
    public static (OllamaApiClient Embedding, OllamaApiClient Chat) CreateClients(AppSettings settings) =>
        (new OllamaApiClient(CreateHttpClient(settings.Ollama), settings.Embedding.Model),
         new OllamaApiClient(CreateHttpClient(settings.Ollama), settings.Ollama.ChatModel));

    /// <summary>The configured Ollama chat model's options - its thinking capability asked of Ollama itself, its window the
    /// num_ctx <see cref="ChatClient"/> sends.</summary>
    public static async Task<ChatModelOptions> ReadChatModelAsync(AppSettings settings, OllamaApiClient chat) =>
        ChatModelOptions.ForOllama(settings.Retrieval, await ChatModelSupportsThinkingAsync(chat, settings.Ollama.ChatModel), settings.Ollama.ContextLength);

    /// <summary>
    /// The chat model as the app asks it: every request carries num_ctx = Ollama:ContextLength, so the window is the
    /// app's, not whatever this machine's Ollama defaults to (it scales with VRAM, and the app's slider or
    /// OLLAMA_CONTEXT_LENGTH change it). A num_ctx already in the options wins - the evaluation's judge raises it. An
    /// evaluation wraps this in its response cache, so the option never reaches the cache keys.
    /// </summary>
    public static IChatClient ChatClient(IChatClient chat, OllamaSettings ollama) => new ChatClientBuilder(chat)
        .ConfigureOptions(options =>
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties.TryAdd(OllamaOption.NumCtx.Name, ollama.ContextLength);
        })
        .Build();

    /// <summary>
    /// The embedding model as the app asks it: truncate false, so a chunk or question past the model's context fails
    /// instead of being cut to fit - /api/embed truncates by default, silently. nomic-embed-text reads 2,048 tokens.
    /// </summary>
    public static IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator(IEmbeddingGenerator<string, Embedding<float>> embedding) => new EmbeddingGeneratorBuilder<string, Embedding<float>>(embedding)
        .ConfigureOptions(options =>
        {
            options.AdditionalProperties ??= [];
            options.AdditionalProperties["truncate"] = false;
        })
        .Build();

    /// <summary>
    /// Refuses a chunk budget past the embedding model's context, read from /api/show - before a build reaches a chunk it
    /// can't embed. The budget is approximate (a chunk can run a little over it, and nomic's tokenizer counts up to ~8%
    /// more than the chunker's), so a budget just under the context can still fail on one chunk - truncate false
    /// (<see cref="EmbeddingGenerator"/>) catches that one.
    /// </summary>
    public static async Task EnsureChunksFitEmbeddingAsync(OllamaApiClient client, string model, int maxTokensPerChunk)
    {
        ShowModelResponse info = await client.ShowModelAsync(model);
        EnsureChunksFitEmbedding(
            info.Info?.ExtraInfo?.FirstOrDefault(kv => kv.Key.EndsWith(".context_length", StringComparison.Ordinal)).Value?.ToString(),
            model, maxTokensPerChunk);
    }

    internal static void EnsureChunksFitEmbedding(string? contextLength, string model, int maxTokensPerChunk)
    {
        if (int.TryParse(contextLength, out int context) && maxTokensPerChunk > context)
        {
            throw new StartupException(
                $"Chunking:MaxTokensPerChunk {maxTokensPerChunk:N0} is past {model}'s context ({context:N0} tokens, Ollama /api/show) - "
                + "its chunks can't be embedded whole. Use a smaller budget, or an embedding model with a longer context (Voyage's read 32,000).");
        }
    }

    /// <summary>
    /// <see cref="AppComposition.OpenExistingIndexAsync"/> embedding questions with the configured Ollama model - or with
    /// <paramref name="queryEmbedding"/>, another provider's, when given - once Ollama is ready. The configured Ollama chat
    /// model answers unless <paramref name="chatClient"/> and <paramref name="chatModel"/> say otherwise - an evaluation
    /// passes a client that caches the model's responses.
    /// </summary>
    public static async Task<RagRuntime> OpenExistingIndexAsync(
        AppSettings settings, DirectoryInfo repoRoot, IChatClient? chatClient = null, ChatModelOptions? chatModel = null,
        IEmbeddingGenerator<string, Embedding<float>>? queryEmbedding = null)
    {
        (OllamaApiClient embedding, OllamaApiClient chat) = CreateClients(settings);
        await EnsureReadyAsync(chat, settings.Ollama, queryEmbedding is null ? [settings.Embedding.Model, settings.Ollama.ChatModel] : [settings.Ollama.ChatModel]);
        if (queryEmbedding is null)
        {
            await EnsureChunksFitEmbeddingAsync(chat, settings.Embedding.Model, settings.Chunking.MaxTokensPerChunk);
        }

        return await AppComposition.OpenExistingIndexAsync(
            settings, repoRoot, queryEmbedding ?? EmbeddingGenerator(embedding), chatClient ?? ChatClient(chat, settings.Ollama),
            chatModel ?? await ReadChatModelAsync(settings, chat));
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
