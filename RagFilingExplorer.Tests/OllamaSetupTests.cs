using Microsoft.Extensions.AI;
using Moq;
using OllamaSharp.Models;
using RagFilingExplorer.Core;
using RagFilingExplorer.Ollama;

namespace RagFilingExplorer.Tests;

[TestFixture]
public class OllamaSetupTests
{
    private static OllamaSettings Ollama(int contextLength) =>
        new() { BaseUrl = "http://localhost:11434/", TimeoutMinutes = 5, ChatModel = "llama3.1:8b", ContextLength = contextLength };

    private static async Task<ChatOptions?> SentOptions(ChatOptions? callerOptions, int contextLength)
    {
        ChatOptions? sent = null;
        Mock<IChatClient> inner = new();
        inner.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((_, options, _) => sent = options)
            .ReturnsAsync(new ChatResponse());

        await OllamaSetup.ChatClient(inner.Object, Ollama(contextLength)).GetResponseAsync("q", callerOptions);
        return sent;
    }

    // Ollama's own default window depends on the machine; the app's is the setting, on every request.
    [Test]
    public async Task ChatClient_EveryRequest_CarriesTheConfiguredWindow()
    {
        ChatOptions caller = new() { Temperature = 0 };

        ChatOptions? sent = await SentOptions(caller, contextLength: 4096);

        Assert.That(sent?.AdditionalProperties?[OllamaOption.NumCtx.Name], Is.EqualTo(4096));
        Assert.That(sent?.Temperature, Is.EqualTo(0));
        Assert.That(caller.AdditionalProperties, Is.Null, "the caller's options aren't changed");
    }

    // The evaluation's judge raises the window around this client; its value must reach Ollama.
    [Test]
    public async Task ChatClient_WindowAlreadySet_KeepsIt()
    {
        ChatOptions caller = new() { AdditionalProperties = new() { [OllamaOption.NumCtx.Name] = 8192 } };

        ChatOptions? sent = await SentOptions(caller, contextLength: 4096);

        Assert.That(sent?.AdditionalProperties?[OllamaOption.NumCtx.Name], Is.EqualTo(8192));
    }

    // /api/embed cuts an over-long input silently unless truncate is false.
    [Test]
    public async Task EmbeddingGenerator_EveryRequest_AsksNotToTruncate()
    {
        EmbeddingGenerationOptions? sent = null;
        Mock<IEmbeddingGenerator<string, Embedding<float>>> inner = new();
        inner.Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken>((_, options, _) => sent = options)
            .ReturnsAsync(new GeneratedEmbeddings<Embedding<float>>());

        await OllamaSetup.EmbeddingGenerator(inner.Object).GenerateAsync(["chunk"]);

        Assert.That(sent?.AdditionalProperties?["truncate"], Is.EqualTo(false));
    }

    // nomic-embed-text through Ollama reads 2,048 tokens; a 10,000-token budget would be cut, silently, chunk by chunk.
    [Test]
    public void EnsureChunksFitEmbedding_BudgetPastTheContext_Throws()
    {
        StartupException ex = Assert.Throws<StartupException>(() => OllamaSetup.EnsureChunksFitEmbedding("2048", "nomic-embed-text", 10000));

        Assert.That(ex.Message, Does.Contain("nomic-embed-text").And.Contain("2,048").And.Contain("Chunking:MaxTokensPerChunk"));
    }

    [TestCase("2048", 500)]
    [TestCase(null, 10000)]
    public void EnsureChunksFitEmbedding_WithinTheContextOrNoneReported_Passes(string? contextLength, int budget)
    {
        Assert.DoesNotThrow(() => OllamaSetup.EnsureChunksFitEmbedding(contextLength, "nomic-embed-text", budget));
    }
}
