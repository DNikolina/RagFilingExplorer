using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>Result of a single retrieve+generate turn.</summary>
internal sealed record RagAnswer(
    string? MatchedFiling,
    string? MatchedStatementType,
    ReasoningEffort UsedReasoningEffort,
    IReadOnlyList<VectorSearchResult<FilingChunkRecord>> RetrievedChunks,
    IAsyncEnumerable<ChatResponseUpdate> AnswerStream);

/// <summary>
/// Owns the retrieve+generate flow: resolving the question's metadata filter (see
/// <see cref="QueryIntentResolver"/>), running the vector search, building the citation-grounded
/// context prompt, and calling the chat model. Extracted out of Program.cs's interactive loop so the
/// two real dependencies (<see cref="VectorStoreCollection{TKey, TRecord}"/> and <see cref="IChatClient"/>)
/// can be mocked in tests instead of requiring a live Ollama instance and a populated vector store.
/// </summary>
internal sealed class RagAnswerService(
    VectorStoreCollection<int, FilingChunkRecord> collection,
    IChatClient chatClient,
    int generationTopK = 5,
    float chatTemperature = 0.2f,
    ReasoningEffort reasoningEffort = ReasoningEffort.None,
    int maxOutputTokens = 2048,
    bool chatModelSupportsThinking = true)
{
    private const string SystemPrompt = """
        You are a financial research assistant answering questions about SEC 10-K filings.
        Answer using ONLY the context excerpts provided below - do not use any outside knowledge about
        these companies, even if you recognize them. For every fact or claim in your answer, cite which
        filing and section it came from, e.g. (Source: MSFT-10K-2026.html, PART II > Item 8. Financial
        Statements and Supplementary Data). If the provided context does not contain enough information
        to answer the question, say so explicitly instead of guessing or relying on prior knowledge.
        """;

    public async Task<RagAnswer> AskAsync(string question, int searchTopK = 5, CancellationToken cancellationToken = default)
    {
        string? targetFiling = QueryIntentResolver.ResolveFiling(question);
        string? targetStatementType = QueryIntentResolver.ResolveStatementType(question);

        VectorSearchOptions<FilingChunkRecord> searchOptions = new();
        if (targetFiling is not null && targetStatementType is not null)
        {
            searchOptions.Filter = r => r.SourceFiling == targetFiling && r.StatementType == targetStatementType;
        }
        else if (targetFiling is not null)
        {
            searchOptions.Filter = r => r.SourceFiling == targetFiling;
        }
        else if (targetStatementType is not null)
        {
            searchOptions.Filter = r => r.StatementType == targetStatementType;
        }

        List<VectorSearchResult<FilingChunkRecord>> results = new();
        await foreach (VectorSearchResult<FilingChunkRecord> result in collection.SearchAsync($"search_query: {question}", searchTopK, searchOptions, cancellationToken))
        {
            results.Add(result);
        }

        List<VectorSearchResult<FilingChunkRecord>> topForGeneration = results.Take(generationTopK).ToList();
        StringBuilder contextBuilder = new();
        for (int i = 0; i < topForGeneration.Count; i++)
        {
            FilingChunkRecord record = topForGeneration[i].Record;
            contextBuilder.AppendLine($"[{i + 1}] Source: {record.SourceFiling} | Section: {record.Heading}");
            contextBuilder.AppendLine(record.Content);
            contextBuilder.AppendLine();
        }

        List<ChatMessage> chatMessages =
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.User, $"Context excerpts:\n\n{contextBuilder}\nQuestion: {question}"),
        ];

        // Reasoning is only worth its cost (extra latency, extra output-token budget) for questions that
        // actually need multi-step synthesis - a plain single-fact lookup gets Effort.None regardless of
        // the configured reasoningEffort. This is what stops a reasoning model from spending its whole
        // generation budget "thinking" about a simple question and never reaching the answer, which is
        // exactly what happened testing qwen3.5:2b as a reference model before this routing existed.
        //
        // chatModelSupportsThinking gates this further, and matters just as much: Ollama doesn't quietly
        // ignore a think request for a model that can't do it - it throws a hard OllamaException
        // ("<model> does not support thinking"), confirmed directly when llama3.1:8b crashed the whole
        // app on the first synthesis question. Program.cs checks the configured chat model's real
        // capabilities via Ollama's own /api/show once at startup, rather than assuming.
        ReasoningEffort effectiveReasoningEffort = chatModelSupportsThinking && QueryIntentResolver.RequiresSynthesis(question)
            ? reasoningEffort
            : ReasoningEffort.None;
        ChatOptions chatOptions = new()
        {
            Temperature = chatTemperature,
            Reasoning = new ReasoningOptions { Effort = effectiveReasoningEffort },
            MaxOutputTokens = maxOutputTokens,
        };

        IAsyncEnumerable<ChatResponseUpdate> rawStream = chatClient.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken);
        IAsyncEnumerable<ChatResponseUpdate> guardedStream = GuardAgainstStarvedResponse(rawStream, cancellationToken);

        return new RagAnswer(targetFiling, targetStatementType, effectiveReasoningEffort, results, guardedStream);
    }

    // A reasoning model can spend its entire MaxOutputTokens budget on "thinking" (see
    // AppSettings.RetrievalSettings.MaxOutputTokens) and hit the token ceiling before ever producing real
    // answer text - Ollama still reports this as a normal completion (FinishReason.Length), so nothing
    // upstream throws and the caller would otherwise just see an empty answer with no explanation. This
    // wraps the raw stream to detect exactly that case and fail loudly instead, while still yielding every
    // update as it arrives so the caller can keep streaming output live.
    private static async IAsyncEnumerable<ChatResponseUpdate> GuardAgainstStarvedResponse(
        IAsyncEnumerable<ChatResponseUpdate> stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool sawRealAnswerText = false;
        ChatFinishReason? finishReason = null;

        await foreach (ChatResponseUpdate update in stream.WithCancellation(cancellationToken))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                sawRealAnswerText = true;
            }

            finishReason = update.FinishReason ?? finishReason;
            yield return update;
        }

        if (!sawRealAnswerText && finishReason == ChatFinishReason.Length)
        {
            throw new InvalidOperationException(
                "The model hit its output token limit without producing an answer - it likely spent the "
                + "whole budget on reasoning (\"thinking\"). Try raising Retrieval.MaxOutputTokens, lowering "
                + "Retrieval.ReasoningEffort, or using a non-reasoning model, in appsettings.json.");
        }
    }
}
