using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using RagFilingExplorer.Local.VectorStore;

namespace RagFilingExplorer.Local.Retrieval;

/// <summary>
/// Result of a single retrieve+generate turn. Under hybrid search, <see cref="MatchedStatementType"/> boosted rather
/// than filtered, <see cref="KeywordQuery"/> is what the keyword search matched (null: no content words), and each
/// retrieved chunk's score is its fused score, not a distance. When reranked, <see cref="Reranked"/> holds each retrieved
/// chunk's (by key) reranker score and its rank before reranking.
/// </summary>
internal sealed record RagAnswer(
    IReadOnlyList<string> MatchedFilings,
    string? MatchedStatementType,
    ReasoningEffort UsedReasoningEffort,
    IReadOnlyList<VectorSearchResult<FilingChunkRecord>> RetrievedChunks,
    IAsyncEnumerable<ChatResponseUpdate> AnswerStream,
    SearchMode Search = SearchMode.Vector,
    string? KeywordQuery = null,
    IReadOnlyDictionary<int, RerankedChunk>? Reranked = null);

/// <summary>A reranked chunk's cross-encoder score and its rank in its company's hybrid list before reranking.</summary>
internal sealed record RerankedChunk(float Score, int HybridRank);

/// <summary>
/// Owns the retrieve+generate flow: resolving the question's company and statement type (see
/// <see cref="CompanyRegistry"/> and <see cref="QueryIntentResolver"/>), running the search - vector, or
/// hybrid (vector + keyword, fused), optionally reranked - building the citation-grounded context prompt, and
/// calling the chat model. Extracted out of Program.cs's interactive loop so the two real dependencies
/// (<see cref="VectorStoreCollection{TKey, TRecord}"/> and <see cref="IChatClient"/>) can be mocked in tests
/// instead of requiring a live Ollama instance and a populated vector store.
///
/// Tunables come in as <see cref="RetrievalSettings"/> with no defaults of their own - an earlier version
/// had constructor defaults that duplicated appsettings.json, and one (maxOutputTokens = 2048 vs. 4096)
/// had already drifted.
/// </summary>
internal sealed class RagAnswerService(
    VectorStoreCollection<int, FilingChunkRecord> collection,
    IChatClient chatClient,
    RetrievalSettings retrieval,
    bool chatModelSupportsThinking,
    CompanyRegistry companies,
    KeywordIndex? keywordIndex = null,
    IRelevanceScorer? reranker = null)
{
    private readonly KeywordIndex? keywords = retrieval.Search == SearchMode.Hybrid
        ? keywordIndex ?? throw new ArgumentNullException(nameof(keywordIndex), "Hybrid search needs the keyword index.")
        : null;

    // Reranking was measured on hybrid candidates only (docs/Decision-Log.md, "Step 2b spike - measured").
    private readonly IRelevanceScorer? reranker = reranker is null || retrieval.Search == SearchMode.Hybrid
        ? reranker
        : throw new ArgumentException("Reranking needs hybrid search - it was measured on hybrid candidates only.", nameof(reranker));

    // Each rule after the first answers a failure seen in the 2026-09-25 manual pass (Decision-Log.md,
    // "manual pass"): bare figures with no unit (7 of 24 main answers - "$45,183,036" for $45.2 billion), a
    // figure named after the wrong one of two near-identical lines (Q10), citations by excerpt number only,
    // pasted pipe-table rows, unrequested and wrong arithmetic, and declines padded with unrelated figures.
    // Two further revisions (2026-09-28) each fixed what they targeted and broke other answers - neither
    // beat this one overall, so it stays; see Decision-Log.md, "manual pass (v1)". The fixed decline form (v2 step 5a)
    // is the one part of that v2 revision that did what it targeted - declines written as the units rule ("The unit is
    // not stated." - H20, and H8, H15, Q15, T2 in earlier runs) - changed alone this time: every decline clean, no answer
    // lost. A first version also said "Do not add figures or units to it." and two NFLX answers dropped "thousand"
    // (T10, H31): a decline-only clause about units leaked into figure answers. Keep unit wording out of this sentence.
    private const string SystemPrompt = """
        You are a financial research assistant answering questions about SEC 10-K filings.
        Answer using ONLY the context excerpts provided below - do not use any outside knowledge about
        these companies, even if you recognize them. If the excerpts do not contain the answer, say so in
        one sentence instead of guessing, in exactly this form: "The excerpts don't contain <what the
        question asks for>."

        Rules for every answer:
        - State each figure with its unit right after the number and its period, e.g. "$55,596,993 thousand
          for the year ended December 31, 2025". Take the unit from the table's units line, such as
          "(in thousands)" or "(In millions)". If no units line applies, say the unit is not stated.
        - When several lines have similar names, use the line whose label matches the question and name
          that line exactly.
        - End each sentence that states a fact with one citation of the filing and section it came from,
          e.g. (Source: MSFT-10K-2026.html, PART II > Item 8. Financial Statements and Supplementary Data).
        - Do not copy table rows or table formatting into the answer; state figures in sentences.
        - Do not calculate anything the question does not ask for. If it does, show the numbers and the
          operation.
        """;

    public async Task<RagAnswer> AskAsync(string question, int searchTopK, CancellationToken cancellationToken = default)
    {
        string[] targetFilings = companies.ResolveFilings(question);
        string? targetStatementType = QueryIntentResolver.ResolveStatementType(question);
        string? keywordQuery = keywords is null ? null : KeywordQuery.Build(question, companies.Registrations.SelectMany(r => r.Names));
        Dictionary<int, RerankedChunk>? reranked = reranker is null ? null : new();

        List<VectorSearchResult<FilingChunkRecord>> results;
        if (targetFilings.Length <= 1)
        {
            results = await RetrieveAsync(question, keywordQuery, searchTopK, targetFilings.SingleOrDefault(), targetStatementType, reranked, cancellationToken);
        }
        else
        {
            // A question naming 2+ companies gets one filtered search per company, interleaved by rank
            // (A1, B1, A2, B2, ...), so every named company is represented in the top results. A single
            // unfiltered search shared its slots across all filings, and "Compare Microsoft's and
            // Oracle's total revenue" lost ORCL's revenue chunk to rank 10.
            int perFilingTopK = (int)Math.Ceiling(searchTopK / (double)targetFilings.Length);
            List<List<VectorSearchResult<FilingChunkRecord>>> perFiling = new();
            foreach (string filing in targetFilings)
            {
                perFiling.Add(await RetrieveAsync(question, keywordQuery, perFilingTopK, filing, targetStatementType, reranked, cancellationToken));
            }

            results = Enumerable.Range(0, perFilingTopK)
                .SelectMany(rank => perFiling.Where(list => rank < list.Count).Select(list => list[rank]))
                .Take(searchTopK)
                .ToList();
        }

        List<VectorSearchResult<FilingChunkRecord>> topForGeneration = results.Take(retrieval.GenerationTopK).ToList();
        // Unnumbered labels in a form that isn't a citation: "[1] Source: X | Section: Y" was a second citation
        // format the model copied ("According to excerpt [1], Source: ..."), sometimes as the only citation -
        // an excerpt number the reader never sees.
        StringBuilder contextBuilder = new();
        foreach (VectorSearchResult<FilingChunkRecord> result in topForGeneration)
        {
            FilingChunkRecord record = result.Record;
            contextBuilder.AppendLine($"--- Excerpt from {record.SourceFiling}, section {record.Heading} ---");
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
        // the configured ReasoningEffort. This is what stops a reasoning model from spending its whole
        // generation budget "thinking" about a simple question and never reaching the answer, which is
        // exactly what happened testing qwen3.5:2b as a reference model before this routing existed.
        //
        // chatModelSupportsThinking gates this further, and matters just as much: Ollama doesn't quietly
        // ignore a think request for a model that can't do it - it throws a hard OllamaException
        // ("<model> does not support thinking"), confirmed directly when llama3.1:8b crashed the whole
        // app on the first synthesis question. OllamaSetup.ChatModelSupportsThinkingAsync checks the chat model's real
        // capabilities via Ollama's own /api/show once at startup, rather than assuming.
        ReasoningEffort effectiveReasoningEffort = chatModelSupportsThinking && QueryIntentResolver.RequiresSynthesis(question)
            ? retrieval.ReasoningEffort
            : ReasoningEffort.None;
        ChatOptions chatOptions = new()
        {
            Temperature = retrieval.ChatTemperature,
            Reasoning = new ReasoningOptions { Effort = effectiveReasoningEffort },
            MaxOutputTokens = retrieval.MaxOutputTokens,
        };

        IAsyncEnumerable<ChatResponseUpdate> rawStream = chatClient.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken);
        IAsyncEnumerable<ChatResponseUpdate> guardedStream = GuardAgainstStarvedResponse(rawStream, cancellationToken);

        return new RagAnswer(targetFilings, targetStatementType, effectiveReasoningEffort, results, guardedStream, retrieval.Search, keywordQuery, reranked);
    }

    private async Task<List<VectorSearchResult<FilingChunkRecord>>> RetrieveAsync(
        string question, string? keywordQuery, int top, string? filing, string? statementType,
        Dictionary<int, RerankedChunk>? reranked, CancellationToken cancellationToken)
    {
        if (keywords is null)
        {
            return await SearchAsync(question, top, filing, statementType, cancellationToken);
        }

        if (reranker is null)
        {
            return await HybridSearchAsync(keywords, question, keywordQuery, top, filing, statementType, cancellationToken);
        }

        // Step 2b: the company's top RerankCandidates, reordered by the cross-encoder, then cut - for a question naming
        // several companies this runs once per company, so each keeps its own share of the slots, as the spike measured.
        List<VectorSearchResult<FilingChunkRecord>> candidates =
            await HybridSearchAsync(keywords, question, keywordQuery, Math.Max(top, retrieval.RerankCandidates), filing, statementType, cancellationToken);
        IReadOnlyList<float> scores = reranker.Score(question, candidates.Select(c => RerankPassage(c.Record)).ToList());
        for (int i = 0; i < candidates.Count; i++)
        {
            reranked![candidates[i].Record.Key] = new RerankedChunk(scores[i], i + 1);
        }

        // OrderByDescending is stable: equal scores keep their hybrid order.
        return candidates.OrderByDescending(c => reranked![c.Record.Key].Score).Take(top).ToList();
    }

    // What the reranker reads for a chunk: its company line (as in its embedding text - the spike's "company" text; without
    // it both models ranked worse than no reranking at all), then the excerpt header the chat model sees, then the chunk.
    private string RerankPassage(FilingChunkRecord record)
    {
        string excerpt = $"Excerpt from {record.SourceFiling}, section {record.Heading}\n{record.Content}";
        string? context = companies.Registrations.FirstOrDefault(r => r.Filing == record.SourceFiling)?.Context;
        return context is null ? excerpt : $"{context}\n{excerpt}";
    }

    // Up to three ranked lists, fused (RankFusion): the vector search and the keyword search within the company filter,
    // and - when the question resolved a statement type - the vector search within that statement too. That third list
    // is the statement label as a boost: a chunk of the named statement gets a second vote, but nothing is excluded, so
    // a question the keyword rules route to the wrong statement (R1: "deferred revenues" -> income statement; the
    // answer is on the balance sheet) can still reach its answer. Measured by replay first - docs/Decision-Log.md,
    // "Step 2 - hybrid search": a hard filter (v1) misses R1, R2 and H14 outright; hybrid without the boost loses
    // statement questions (Q23 rank 1 -> 11); keyword + vector + this one boosting list beat or tied every other mix.
    private async Task<List<VectorSearchResult<FilingChunkRecord>>> HybridSearchAsync(
        KeywordIndex keywordIndex, string question, string? keywordQuery, int top, string? filing, string? statementType,
        CancellationToken cancellationToken)
    {
        int depth = retrieval.HybridCandidates;
        List<VectorSearchResult<FilingChunkRecord>> semantic = await SearchAsync(question, depth, filing, null, cancellationToken);
        List<FilingChunkRecord> keyword = keywordQuery is null ? [] : keywordIndex.Search(keywordQuery, filing, depth);
        List<VectorSearchResult<FilingChunkRecord>> boost = statementType is null
            ? []
            : await SearchAsync(question, depth, filing, statementType, cancellationToken);

        Dictionary<int, FilingChunkRecord> records = new();
        foreach (FilingChunkRecord record in semantic.Concat(boost).Select(r => r.Record).Concat(keyword))
        {
            records.TryAdd(record.Key, record);
        }

        return RankFusion.Fuse([semantic.Select(r => r.Record.Key).ToList(), keyword.Select(r => r.Key).ToList(), boost.Select(r => r.Record.Key).ToList()])
            .Take(top)
            .Select(fused => new VectorSearchResult<FilingChunkRecord>(records[fused.Key], fused.Score))
            .ToList();
    }

    private async Task<List<VectorSearchResult<FilingChunkRecord>>> SearchAsync(
        string question, int top, string? filing, string? statementType, CancellationToken cancellationToken)
    {
        VectorSearchOptions<FilingChunkRecord> searchOptions = new();
        if (filing is not null && statementType is not null)
        {
            searchOptions.Filter = r => r.SourceFiling == filing && r.StatementType == statementType;
        }
        else if (filing is not null)
        {
            searchOptions.Filter = r => r.SourceFiling == filing;
        }
        else if (statementType is not null)
        {
            searchOptions.Filter = r => r.StatementType == statementType;
        }

        List<VectorSearchResult<FilingChunkRecord>> results = new();
        await foreach (VectorSearchResult<FilingChunkRecord> result in collection.SearchAsync($"search_query: {question}", top, searchOptions, cancellationToken))
        {
            results.Add(result);
        }

        return results;
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
